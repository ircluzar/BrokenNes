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
    _lastRafTs: 0, _skipsThisBurst: 0, _targetFps: 60, _maxFrameSkips: 1, _frameAccumMs: 0,

    startEmulationLoop(dotNetRef) {
        this._dotNetRef = dotNetRef;
        this._loopToken = (this._loopToken + 1) | 0;
        if (this._rafId != null) { try { cancelAnimationFrame(this._rafId); } catch { } this._rafId = null; }
        this._loopActive = true; this._lastRafTs = 0; this._skipsThisBurst = 0; this._frameAccumMs = 0;
        const token = this._loopToken;
        const step = async (ts) => {
            if (!this._loopActive || token !== this._loopToken) return;
            const targetMs = 1000 / this._targetFps;
            const now = ts || performance.now();
            const dt = this._lastRafTs ? (now - this._lastRafTs) : targetMs;
            this._lastRafTs = now;

            // Pace emulation to _targetFps regardless of display refresh rate. rAF fires at the
            // display's own rate (which can be 120/144/165 Hz), and without this gate every
            // callback ran a full emulated frame - fine at 60 Hz, but on a faster display (or
            // once AOT makes a frame cheap enough to keep up) the game ran 2-2.4x too fast and
            // audio was produced faster than the 44.1kHz sink could drain it, so playback
            // latency grew without bound. Accumulate elapsed time and only emulate once a full
            // frame interval has passed; cap the accumulator so a long stall (backgrounded tab,
            // debugger pause) can't trigger a burst of catch-up frames on return.
            this._frameAccumMs = Math.min(this._frameAccumMs + dt, targetMs * 4);
            if (this._frameAccumMs < targetMs) {
                this._rafId = requestAnimationFrame(step);
                return;
            }
            this._frameAccumMs -= targetMs;
            const behind = this._frameAccumMs > targetMs * 0.5;

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

    // Below 60 fps (the norm without AOT - see Web/README.md), each buffer only holds
    // 1/60s of audio but arrives slower than every 1/60s, so scheduling it at its nominal
    // duration falls behind real time on nearly every call. The old fix-up (jump the
    // timeline to ctx.currentTime) covered the gap with silence - audible as constant
    // crackle. Instead, stretch each buffer's playbackRate to span the actual wall-clock
    // gap since the previous one, so playback stays continuous (at a correspondingly
    // lower pitch when emulation is slow) instead of alternating audio/silence. The rate
    // is clamped so pitch drift stays modest even under a large stall.
    _lastPlayAudioAt: 0,
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

            const now = ctx.currentTime;
            const nominalDuration = buffer.duration;
            const observedGap = this._lastPlayAudioAt ? (now - this._lastPlayAudioAt) : nominalDuration;
            this._lastPlayAudioAt = now;
            let rate = observedGap > 0.001 ? nominalDuration / observedGap : 1.0;
            rate = Math.min(Math.max(rate, 0.5), 1.5);
            try { src.playbackRate.value = rate; } catch { }

            // Bound the lead in both directions: below, top up so playback doesn't try to
            // start in the past; above, clamp so a burst (e.g. tab regaining focus) can't
            // schedule minutes of audio into the future - see the frame-pacing fix above
            // for why that no longer happens in steady state, this is a safety net.
            const maxLeadSeconds = 0.25;
            if (window._nesAudioTimeline < now) window._nesAudioTimeline = now + 0.01;
            if (window._nesAudioTimeline > now + maxLeadSeconds) window._nesAudioTimeline = now + maxLeadSeconds;

            try { src.start(window._nesAudioTimeline); } catch { }
            window._nesActiveSources.push(src);
            if (window._nesActiveSources.length > 64) window._nesActiveSources.splice(0, 32);
            window._nesAudioTimeline += nominalDuration / rate;
        } catch { }
    },

    flushAudioOutput() {
        try {
            (window._nesActiveSources || []).forEach(s => { try { s.stop(); } catch { } });
            window._nesActiveSources = [];
            if (window.nesAudioCtx) window._nesAudioTimeline = window.nesAudioCtx.currentTime + 0.02;
            // Otherwise the next playAudio() measures the pause itself as an "observed gap"
            // and stretches its first buffer to match, producing a slow-motion pitch drop.
            this._lastPlayAudioAt = 0;
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
        // Also exempt SELECT/BUTTON so the page's own dropdowns and Play/Pause/Reset buttons
        // stay keyboard-operable (arrow keys navigate a focused <select>; Enter/Space activate
        // a focused <button>) instead of being swallowed as D-pad/Start input.
        const blocksGameInput = () => {
            const a = document.activeElement;
            if (!a) return false;
            const t = a.tagName;
            return t === 'INPUT' || t === 'TEXTAREA' || t === 'SELECT' || t === 'BUTTON' || a.isContentEditable;
        };
        const apply = (e, down) => {
            // Let OS/browser chords through untouched (Ctrl+A, Cmd+S, Alt+D, ...) - several
            // single-letter shortcuts collide with the WASD map and would otherwise both
            // block the browser action and inject a phantom press into the emulator.
            if (e.ctrlKey || e.metaKey || e.altKey) return;
            const i = map[e.code];
            if (i === undefined || blocksGameInput()) return;
            e.preventDefault();
            if (window.nesInputState[i] === down) return;
            window.nesInputState[i] = down;
            try { this._mainRef && this._mainRef.invokeMethodAsync('UpdateInput', window.nesInputState); } catch { }
        };
        document.addEventListener('keydown', e => apply(e, true));
        document.addEventListener('keyup', e => apply(e, false));

        // Alt-tabbing (or clicking outside the page) away while a key is held never delivers
        // its keyup to this document, so without this the direction/button stays latched and
        // drives the game indefinitely after the user returns.
        const clearAllInputs = () => {
            let changed = false;
            for (let i = 0; i < window.nesInputState.length; i++) {
                if (window.nesInputState[i]) { window.nesInputState[i] = false; changed = true; }
            }
            if (changed) { try { this._mainRef && this._mainRef.invokeMethodAsync('UpdateInput', window.nesInputState); } catch { } }
        };
        window.addEventListener('blur', clearAllInputs);
        document.addEventListener('visibilitychange', () => {
            if (document.visibilityState === 'hidden') clearAllInputs();
        });
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
