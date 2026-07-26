// BrokenNes Web - minimal interop. Distilled from origin/webonly:wwwroot/lib/nesInterop.js.
// NO shaders, NO WebGL, NO soundfont, NO corruptor, NO story/music.
// Video = 2D putImageData blit. Audio = AudioBufferSourceNode scheduling.
window.nesInterop = {

    // ---------------- video ----------------
    _cache: {},
    _ensureCanvasCache(canvasId) {
        const c = document.getElementById(canvasId);
        if (!c) return null;
        let cache = this._cache[canvasId];
        if (!cache) {
            const off = document.createElement('canvas');
            off.width = 256; off.height = 240;
            const offCtx = off.getContext('2d');
            cache = { canvas: c, off, offCtx, imageData: offCtx.createImageData(256, 240), ctx: c.getContext('2d') };
            this._cache[canvasId] = cache;
        }
        return cache;
    },

    drawFrame(canvasId, framebuffer) {
        const cache = this._ensureCanvasCache(canvasId);
        if (!cache) return;
        const { offCtx, off, imageData, canvas, ctx } = cache;
        if (framebuffer && framebuffer.length >= 256 * 240 * 4) {
            // byte[] from .NET arrives as Uint8Array; plain Array also tolerated.
            try { imageData.data.set(framebuffer); } catch { }
        }
        offCtx.putImageData(imageData, 0, 0);
        ctx.imageSmoothingEnabled = false;
        ctx.drawImage(off, 0, 0, canvas.width, canvas.height);
    },

    presentFrame(canvasId, framebuffer, audioBuffer, sampleRate) {
        if (audioBuffer && audioBuffer.length) this.playAudio(audioBuffer, sampleRate || 44100);
        if (framebuffer) this.drawFrame(canvasId, framebuffer);
    },

    // ---------------- frame loop (FMC / rAF) ----------------
    _dotNetRef: null, _loopActive: false, _loopToken: 0, _rafId: null,
    _lastRafTs: 0, _skipsThisBurst: 0, _targetFps: 60, _maxFrameSkips: 1,

    startEmulationLoop(dotNetRef) {
        this._dotNetRef = dotNetRef;
        this._loopToken = (this._loopToken + 1) | 0;
        if (this._rafId != null) { try { cancelAnimationFrame(this._rafId); } catch { } this._rafId = null; }
        this._loopActive = true; this._lastRafTs = 0; this._skipsThisBurst = 0;
        const token = this._loopToken;
        const step = async (ts) => {
            if (!this._loopActive || token !== this._loopToken) return;
            const targetMs = 1000 / this._targetFps;
            const dt = this._lastRafTs ? (ts - this._lastRafTs) : targetMs;
            const behind = dt > targetMs * 1.5;
            this._lastRafTs = ts || performance.now();
            if (this._dotNetRef) {
                try {
                    const r = await this._dotNetRef.invokeMethodAsync('FrameTick');
                    if (r && token === this._loopToken && this._loopActive) {
                        const fb = r.fb || r.Framebuffer;
                        const audio = r.audio || r.Audio;
                        const sr = r.sr || r.SampleRate || 44100;
                        const haveAudio = !!(audio && audio.length);
                        const canSkip = behind && this._skipsThisBurst < this._maxFrameSkips;
                        const fbToPresent = canSkip ? null : fb;
                        if (fbToPresent || haveAudio) this.presentFrame('nes-canvas', fbToPresent, audio, sr);
                        this._skipsThisBurst = canSkip ? this._skipsThisBurst + 1 : 0;
                    }
                } catch { }
            }
            if (!this._loopActive || token !== this._loopToken) return;
            this._rafId = requestAnimationFrame(step);
        };
        this._rafId = requestAnimationFrame(step);
    },

    stopEmulationLoop() {
        this._loopActive = false;
        this._loopToken = (this._loopToken + 1) | 0;
        if (this._rafId != null) { try { cancelAnimationFrame(this._rafId); } catch { } this._rafId = null; }
        this._dotNetRef = null;
    },

    // ---------------- clock id ----------------
    _activeClockId: '',
    setActiveClockId(id) { this._activeClockId = id || ''; },
    getActiveClockId() { return this._activeClockId; },

    // ---------------- audio ----------------
    _masterVolume: 1.0,
    ensureAudioContext() {
        try {
            if (!window.nesAudioCtx) window.nesAudioCtx = new (window.AudioContext || window.webkitAudioContext)();
            const ctx = window.nesAudioCtx;
            if (ctx.state === 'suspended') ctx.resume();
            if (!window._nesMasterGain) {
                const g = ctx.createGain();
                g.gain.value = this._masterVolume;
                g.connect(ctx.destination);
                window._nesMasterGain = g;
            }
            if (!window._nesActiveSources) window._nesActiveSources = [];
            if (!window._nesAudioTimeline) window._nesAudioTimeline = ctx.currentTime + 0.02;
            return ctx.state;
        } catch (e) { return 'error'; }
    },

    playAudio(audioBuffer, sampleRate) {
        try {
            this.ensureAudioContext();
            const ctx = window.nesAudioCtx;
            if (!ctx || !audioBuffer || !audioBuffer.length) return;
            const sr = (typeof sampleRate === 'number' && sampleRate > 0) ? sampleRate : 44100;
            const buffer = ctx.createBuffer(1, audioBuffer.length, sr);
            const ch = buffer.getChannelData(0);
            for (let i = 0; i < audioBuffer.length; i++) ch[i] = audioBuffer[i] || 0;
            const src = ctx.createBufferSource();
            src.buffer = buffer;
            src.connect(window._nesMasterGain);
            if (window._nesAudioTimeline < ctx.currentTime) window._nesAudioTimeline = ctx.currentTime + 0.01;
            try { src.start(window._nesAudioTimeline); } catch { }
            window._nesActiveSources.push(src);
            if (window._nesActiveSources.length > 64) window._nesActiveSources.splice(0, 32);
            window._nesAudioTimeline += buffer.duration;
        } catch { }
    },

    flushAudioOutput() {
        try {
            (window._nesActiveSources || []).forEach(s => { try { s.stop(); } catch { } });
            window._nesActiveSources = [];
            if (window.nesAudioCtx) window._nesAudioTimeline = window.nesAudioCtx.currentTime + 0.02;
        } catch { }
    },
    resetAudioTimeline() { this.flushAudioOutput(); },
    setMasterVolume(v) {
        this._masterVolume = Math.max(0, Math.min(1, v));
        try { if (window._nesMasterGain) window._nesMasterGain.gain.value = this._masterVolume; } catch { }
    },

    // ---------------- input ----------------
    // Index order is fixed by the cores: 0=Up 1=Down 2=Left 3=Right 4=A 5=B 6=Select 7=Start
    _mainRef: null, _kbdInstalled: false,
    setMainRef(ref) { this._mainRef = ref; },

    registerInput(dotNetRef) {
        this._mainRef = dotNetRef;
        if (!window.nesInputState) window.nesInputState = new Array(8).fill(false);
        if (!window.nesInputStateP2) window.nesInputStateP2 = new Array(8).fill(false);
        this._ensureEmuFocusHooks();
        if (this._kbdInstalled) return;
        this._kbdInstalled = true;
        const map = {
            ArrowUp: 0, ArrowDown: 1, ArrowLeft: 2, ArrowRight: 3,
            KeyW: 0, KeyS: 1, KeyA: 2, KeyD: 3,
            KeyX: 4, KeyZ: 5, Space: 6, Enter: 7
        };
        const editable = () => {
            const a = document.activeElement;
            return a && (a.tagName === 'INPUT' || a.tagName === 'TEXTAREA' || a.isContentEditable);
        };
        const apply = (e, down) => {
            const i = map[e.code];
            if (i === undefined || editable()) return;
            e.preventDefault();
            if (window.nesInputState[i] === down) return;
            window.nesInputState[i] = down;
            try { this._mainRef && this._mainRef.invokeMethodAsync('UpdateInput', window.nesInputState); } catch { }
        };
        document.addEventListener('keydown', e => apply(e, true));
        document.addEventListener('keyup', e => apply(e, false));
    },

    _ensureEmuFocusHooks() {
        const c = document.getElementById('nes-canvas');
        if (!c || c._nesHooked) return;
        c._nesHooked = true;
        c.setAttribute('tabindex', '0');
        c.addEventListener('pointerdown', () => { try { c.focus(); } catch { } this.ensureAudioContext(); });
    },

    // ---------------- visibility ----------------
    registerVisibility(dotNetRef) {
        if (this._visRegistered) return;
        this._visRegistered = true;
        document.addEventListener('visibilitychange', () => {
            try { dotNetRef.invokeMethodAsync('JsVisibilityChanged', document.visibilityState !== 'hidden'); } catch { }
        });
    },

    // ---------------- IndexedDB kv (prefs) ----------------
    _db: null,
    _openDb() {
        if (this._db) return this._db;
        this._db = new Promise((resolve, reject) => {
            const req = indexedDB.open('nesStorage', 1);
            req.onupgradeneeded = () => {
                const db = req.result;
                if (!db.objectStoreNames.contains('kv')) db.createObjectStore('kv', { keyPath: 'key' });
                if (!db.objectStoreNames.contains('roms')) db.createObjectStore('roms', { keyPath: 'name' });
            };
            req.onsuccess = () => resolve(req.result);
            req.onerror = () => reject(req.error);
        });
        return this._db;
    },
    async idbSetItem(key, value) {
        try {
            const db = await this._openDb();
            await new Promise((res, rej) => {
                const tx = db.transaction('kv', 'readwrite');
                tx.objectStore('kv').put({ key, value });
                tx.oncomplete = res; tx.onerror = () => rej(tx.error);
            });
        } catch { }
    },
    async idbGetItem(key) {
        try {
            const db = await this._openDb();
            return await new Promise((res) => {
                const tx = db.transaction('kv', 'readonly');
                const r = tx.objectStore('kv').get(key);
                r.onsuccess = () => res(r.result ? r.result.value : null);
                r.onerror = () => res(null);
            });
        } catch { return null; }
    }
};
