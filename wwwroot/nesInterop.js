// nesInterop.js - Blazor JS interop for NES framebuffer drawing
window.nesInterop = {
    _cache: {},
    _loopActive: false,
    _dotNetRef: null,
    _rafId: null,
    _lastFpsTime: 0,
    _framesThisSecond: 0,
    _gl: null,
    _glObjects: null,
    // Deprecated single toggle flags replaced by registry-driven system
    _shaderEnabled: true, // kept for backward compatibility (toggle between first two registered shaders if desired)
    _rfStrength: 1.35, // Default RF strength (uniform uStrength if present)
    _basicLogged: false,
    _dbPromise: null,
    // ================= Shader Registry (extensible) =================
    // Internal registry: key -> { displayName, fragment, vertex(optional), meta }
    _shaderRegistry: {},
    // Compiled program objects: key -> { program, aPos, aTex, uTime, uTexSize, uStrength }
    _shaderPrograms: {},
    _sharedVertexSource: "attribute vec2 aPos;attribute vec2 aTex;varying vec2 vTex;void main(){vTex=aTex;gl_Position=vec4(aPos,0.0,1.0);}",
    _activeShaderKey: 'RF', // default
    registerShader(key, displayName, fragmentSrc, options){
        if(!key||!fragmentSrc) return;
        this._shaderRegistry[key]={ key, displayName: displayName||key, fragment: fragmentSrc, options: options||{} };
    },
    getShaderOptions(){
        if(Object.keys(this._shaderRegistry).length===0){
            // In case initialization path (WebGL init) not hit yet
            this._registerBuiltInShaders();
        }
        return Object.keys(this._shaderRegistry).map(k=>({ key: k, label: this._shaderRegistry[k].displayName }));
    },
    getActiveShader(){ return this._activeShaderKey; },
    setShader(key){
        if(Object.keys(this._shaderRegistry).length===0){
            this._registerBuiltInShaders();
        }
        if(!this._shaderRegistry[key]) return this._shaderRegistry[this._activeShaderKey]?.displayName||'';
        this._activeShaderKey = key;
        // ensure programs built
        if(this._gl && this._glObjects && !this._shaderPrograms[key]){
            this._buildAllShaderPrograms();
        }
        return this._shaderRegistry[key].displayName;
    },
    // Legacy toggle (kept so existing C# calls won't break if any remain)
    toggleShader(){
        const keys = Object.keys(this._shaderRegistry);
        if(keys.length<2) return this._shaderEnabled;
        const idx = keys.indexOf(this._activeShaderKey);
        const next = keys[(idx+1)%keys.length];
        this.setShader(next);
        this._shaderEnabled = true; // always enabled in new system
        return true;
    },

    // ================= IndexedDB Helpers =================
    _openDb() {
        if (this._dbPromise) return this._dbPromise;
        this._dbPromise = new Promise((resolve, reject) => {
            try {
                const req = indexedDB.open('nesStorage', 1);
                req.onupgradeneeded = (e) => {
                    const db = req.result;
                    if (!db.objectStoreNames.contains('roms')) {
                        db.createObjectStore('roms', { keyPath: 'name' });
                    }
                    if (!db.objectStoreNames.contains('kv')) {
                        const kv = db.createObjectStore('kv', { keyPath: 'key' });
                        kv.createIndex('key_idx', 'key', { unique: true });
                    }
                };
                req.onsuccess = () => resolve(req.result);
                req.onerror = () => reject(req.error || new Error('IndexedDB open error'));
            } catch (e) { reject(e); }
        });
        return this._dbPromise;
    },
    _tx(store, mode) {
        return this._openDb().then(db => db.transaction(store, mode).objectStore(store));
    },
    async idbSetItem(key, value) {
        try {
            const store = await this._tx('kv', 'readwrite');
            await new Promise((res, rej) => {
                const r = store.put({ key, value });
                r.onsuccess = () => res();
                r.onerror = () => rej(r.error);
            });
        } catch (e) { console.warn('idbSetItem failed', e); }
    },
    async idbGetItem(key) {
        try {
            const store = await this._tx('kv', 'readonly');
            return await new Promise((res, rej) => {
                const r = store.get(key);
                r.onsuccess = () => res(r.result ? r.result.value : null);
                r.onerror = () => rej(r.error);
            });
        } catch { return null; }
    },
    async idbRemoveItem(key) {
        try {
            const store = await this._tx('kv', 'readwrite');
            await new Promise((res, rej) => { const r = store.delete(key); r.onsuccess = () => res(); r.onerror = () => rej(r.error); });
        } catch {}
    },
    async idbKeys(prefix) {
        try {
            const store = await this._tx('kv', 'readonly');
            return await new Promise((res, rej) => {
                const keys = [];
                const req = store.openCursor();
                req.onsuccess = (e) => {
                    const cur = e.target.result;
                    if (cur) {
                        if (!prefix || cur.key.startsWith(prefix)) keys.push(cur.key);
                        cur.continue();
                    } else res(keys);
                };
                req.onerror = () => rej(req.error);
            });
        } catch { return []; }
    },
    // State helpers mimic localStorage key pattern used previously
    async saveStateChunk(key, value){ await this.idbSetItem(key, value); },
    async getStateChunk(key){ return await this.idbGetItem(key); },
    async removeStateKey(key){ await this.idbRemoveItem(key); },
    async saveRom(name, base64) {
        try {
            const store = await this._tx('roms', 'readwrite');
            await new Promise((res, rej) => { const r = store.put({ name, base64 }); r.onsuccess = () => res(); r.onerror = () => rej(r.error); });
        } catch (e) { console.warn('saveRom failed', e); }
    },
    async getStoredRoms() {
        try {
            const store = await this._tx('roms', 'readonly');
            return await new Promise((res, rej) => {
                if ('getAll' in store) {
                    const r = store.getAll();
                    r.onsuccess = () => res(r.result || []);
                    r.onerror = () => rej(r.error);
                } else {
                    const out = [];
                    const cursorReq = store.openCursor();
                    cursorReq.onsuccess = (e) => {
                        const cur = e.target.result;
                        if (cur) { out.push(cur.value); cur.continue(); } else res(out); };
                    cursorReq.onerror = () => rej(cursorReq.error);
                }
            });
        } catch { return []; }
    },
    async removeStoredRom(name) {
        try {
            const store = await this._tx('roms', 'readwrite');
            await new Promise((res, rej) => { const r = store.delete(name); r.onsuccess = () => res(); r.onerror = () => rej(r.error); });
        } catch {}
    },

    ensureAudioContext(){
        try {
            if(!window.nesAudioCtx){
                window.nesAudioCtx = new (window.AudioContext || window.webkitAudioContext)();
            }
            const ctx = window.nesAudioCtx;
            if(ctx.state === 'suspended'){
                ctx.resume();
            }
            // Prime timeline so first buffer schedules slightly ahead
            window._nesAudioTimeline = ctx.currentTime + 0.02;
        } catch(e){ console.warn('ensureAudioContext failed', e); }
    },
    resetAudioTimeline(){
        try{
            if(!window.nesAudioCtx){
                window.nesAudioCtx = new (window.AudioContext || window.webkitAudioContext)();
            }
            const ctx = window.nesAudioCtx;
            if(ctx.state === 'suspended'){
                ctx.resume();
            }
            // Reset scheduling to just ahead of current time to avoid gaps/overlaps after state loads
            window._nesAudioTimeline = ctx.currentTime + 0.02;
        }catch(e){ console.warn('resetAudioTimeline failed', e); }
    },
    // ================= Title Screen Music =================
    _titleMusicEl: null,
    _titleMusicGain: null,
    _titleMusicSrc: null,
    ensureTitleMusic(){
        try {
            this.ensureAudioContext();
            const ctx = window.nesAudioCtx;
            if(!this._titleMusicEl){
                const el = document.getElementById('titleMusic');
                if(!el){
                    // Fallback create (hidden)
                    const created = document.createElement('audio');
                    created.id='titleMusic';
                    created.src='TitleScreen.mp3';
                    created.loop=true; created.preload='auto';
                    created.style.display='none';
                    document.body.appendChild(created);
                    this._titleMusicEl = created;
                } else { this._titleMusicEl = el; }
            }
            if(!this._titleMusicGain){
                this._titleMusicGain = ctx.createGain();
                this._titleMusicGain.gain.value = 0; // will fade in
            }
            if(!this._titleMusicSrc){
                this._titleMusicSrc = ctx.createMediaElementSource(this._titleMusicEl);
                this._titleMusicSrc.connect(this._titleMusicGain).connect(ctx.destination);
            }
        } catch(e){ console.warn('ensureTitleMusic failed', e); }
    },
    playTitleMusic(){
        try {
            this.ensureTitleMusic();
            const ctx = window.nesAudioCtx;
            const g = this._titleMusicGain;
            if(ctx.state==='suspended') ctx.resume();
            const el = this._titleMusicEl;
            if(el && el.paused){ el.currentTime=0; el.play().catch(()=>{}); }
            const now = ctx.currentTime;
            g.gain.cancelScheduledValues(now);
            g.gain.setValueAtTime(g.gain.value, now);
            g.gain.linearRampToValueAtTime(0.42, now + 1.0);
        } catch(e){ console.warn('playTitleMusic failed', e); }
    },
    fadeOutAndStopTitleMusic(){
        try {
            if(!this._titleMusicGain || !window.nesAudioCtx) return;
            const ctx = window.nesAudioCtx;
            const g = this._titleMusicGain;
            const el = this._titleMusicEl;
            const now = ctx.currentTime;
            g.gain.cancelScheduledValues(now);
            g.gain.setValueAtTime(g.gain.value, now);
            g.gain.linearRampToValueAtTime(0.0, now + 1.2);
            // After fade complete, pause element
            setTimeout(()=>{ try { if(el) el.pause(); } catch{} }, 1250);
        } catch(e){ console.warn('fadeOutAndStopTitleMusic failed', e); }
    },
    // ====== SharedArrayBuffer + AudioWorklet ring (HotPot HOTPOT-04) ======
    _awEnabled: false,
    _awFailed: false,
    _awCtrl: null, // Int32Array [read, write, capacity, dropped]
    _awBuf: null, // Float32Array ring
    _awCapacity: 0,
    _awPendingWorklet: false,
    _awFeatureFlagChecked: false,
    _awMinLeadSamples: 2048, // attempt to keep at least this many queued (approx 46ms @44.1k)
    async _initAudioWorkletIfNeeded(sampleRate){
        if(this._awEnabled || this._awFailed || this._awPendingWorklet) return this._awEnabled;
        // Feature flag via query (?featureAudioSAB=1) to allow progressive rollout
        if(!this._awFeatureFlagChecked){
            try { this._awOn = await this.hasQueryFlag('featureAudioSAB'); } catch { this._awOn=false; }
            this._awFeatureFlagChecked = true;
        }
        if(!this._awOn) return false;
        try {
            if(!window.crossOriginIsolated){
                console.warn('[NES] SharedArrayBuffer unavailable (missing COOP/COEP); falling back.');
                this._awFailed = true; return false;
            }
            if(!window.nesAudioCtx){
                window.nesAudioCtx = new (window.AudioContext || window.webkitAudioContext)({ sampleRate: sampleRate || 44100});
            }
            const ctx = window.nesAudioCtx;
            if(ctx.audioWorklet){
                this._awPendingWorklet = true;
                // Ring capacity: 0.25s of audio rounded to power-of-two
                const targetSamples = Math.ceil((sampleRate||44100) * 0.25);
                let cap = 1; while(cap < targetSamples) cap <<=1; // P2 for cheap wrap
                const sab = new SharedArrayBuffer(cap * 4); // Float32
                const ctrl = new SharedArrayBuffer(4*4); // 4 Int32 slots
                this._awBuf = new Float32Array(sab);
                this._awCtrl = new Int32Array(ctrl);
                this._awCapacity = cap;
                Atomics.store(this._awCtrl,0,0); // read
                Atomics.store(this._awCtrl,1,0); // write
                Atomics.store(this._awCtrl,2,cap); // capacity
                Atomics.store(this._awCtrl,3,0); // dropped
                try { await ctx.audioWorklet.addModule('audio-worklet.js'); } catch(e){ console.warn('worklet addModule failed', e); throw e; }
                const node = new AudioWorkletNode(ctx, 'nes-ring-proc', { processorOptions: { sab, ctrl } });
                node.connect(ctx.destination);
                this._awEnabled = true;
                console.log('[NES] AudioWorklet ring enabled. Capacity', cap);
            } else {
                this._awFailed = true; return false;
            }
        } catch(e){
            console.warn('[NES] AudioWorklet init failed; fallback to buffer scheduling.', e);
            this._awFailed = true;
        } finally { this._awPendingWorklet = false; }
        return this._awEnabled;
    },
    _awWrite(samples){
        const ctrl = this._awCtrl; const buf = this._awBuf; if(!ctrl||!buf) return false;
        let r = Atomics.load(ctrl,0); let w = Atomics.load(ctrl,1); const cap = buf.length;
        for(let i=0;i<samples.length;i++){
            const nextW = (w+1) === cap ? 0 : (w+1);
            if(nextW === r){
                // Buffer full -> drop sample
                const dropped = Atomics.add(ctrl,3,1);
                break; // stop writing more to avoid spinning
            }
            buf[w] = samples[i];
            w = nextW;
        }
        Atomics.store(ctrl,1,w);
        return true;
    },
    audioDiag(){
        const ctrl = this._awCtrl; return {
            enabled: this._awEnabled,
            failed: this._awFailed,
            capacity: this._awCapacity,
            dropped: ctrl? Atomics.load(ctrl,3):0,
            read: ctrl? Atomics.load(ctrl,0):0,
            write: ctrl? Atomics.load(ctrl,1):0
        };
    },
    // Unified presentFrame: canvas draw + optional audio buffer write (legacy OR ring)
    presentFrame: async function(canvasId, framebuffer, audioBuffer, sampleRate){
        try {
            // Attempt worklet init (once) when first audio arrives
            if(audioBuffer && audioBuffer.length){
                await this._initAudioWorkletIfNeeded(sampleRate);
            }
            if(this._awEnabled && audioBuffer && audioBuffer.length){
                this._awWrite(audioBuffer);
            } else if(audioBuffer && audioBuffer.length) {
                // legacy path -> schedule buffer copy
                this.playAudio(audioBuffer, sampleRate||44100);
            }
            if(framebuffer){ this.drawFrame(canvasId, framebuffer); }
        } catch(e){ console.warn('presentFrame failed', e); }
    },

    _ensureCanvasCache(canvasId) {
        const c = document.getElementById(canvasId);
        if (!c) { console.error(`Canvas with id '${canvasId}' not found`); return null; }
        let cache = this._cache[canvasId];
        if (!cache) {
            const off = document.createElement('canvas');
            off.width = 256; off.height = 240;
            const offCtx = off.getContext('2d');
            const imageData = offCtx.createImageData(256, 240);
            cache = { canvas: c, off, offCtx, imageData, ctx: null };
            this._cache[canvasId] = cache;
        }
        return cache;
    },

    drawFrame: function (canvasId, framebuffer) {
        // Initialize fallback 2D cache (always keeps imageData for texture upload or direct blit)
        const cache = this._ensureCanvasCache(canvasId);
        if (!cache) return;
    const { offCtx, off, imageData, canvas } = cache;

        // Update pixel buffer
        if (framebuffer && framebuffer.length >= 256 * 240 * 4) {
            imageData.data.set(framebuffer);
        } else if (!cache._cleared) {
            for (let i = 0; i < imageData.data.length; i += 4) {
                imageData.data[i] = 0; imageData.data[i+1]=0; imageData.data[i+2]=0; imageData.data[i+3]=255;
            }
            cache._cleared = true;
        }

        // Initialize WebGL once; if unavailable we permanently fall back to 2D
        const webglAvailable = this._initWebGL(canvas);
        if (!webglAvailable) {
            if (!cache.ctx) cache.ctx = canvas.getContext('2d');
            const ctx = cache.ctx;
            offCtx.putImageData(imageData, 0, 0);
            ctx.imageSmoothingEnabled = false;
            ctx.clearRect(0,0,canvas.width,canvas.height);
            ctx.drawImage(off,0,0,canvas.width,canvas.height);
            return;
        }

        const gl = this._gl;
        const glRes = this._glObjects;
        const isMSH = this._activeShaderKey === 'MSH';

        // --- Previous frame texture setup (only for MSH shader) ---
        if (isMSH && !glRes.prevFrameTexture) {
            // Create previous frame texture and FBO
            glRes.prevFrameTexture = gl.createTexture();
            gl.bindTexture(gl.TEXTURE_2D, glRes.prevFrameTexture);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
            gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, 256, 240, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
            glRes.prevFrameFBO = gl.createFramebuffer();
        }

        // Upload/refresh main NES texture
        gl.bindTexture(gl.TEXTURE_2D, glRes.texture);
        if (!glRes.initialized) {
            gl.pixelStorei(gl.UNPACK_ALIGNMENT, 1);
            gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, 256, 240, 0, gl.RGBA, gl.UNSIGNED_BYTE, imageData.data);
            glRes.initialized = true;
        } else {
            gl.texSubImage2D(gl.TEXTURE_2D, 0, 0, 0, 256, 240, gl.RGBA, gl.UNSIGNED_BYTE, imageData.data);
        }
        gl.viewport(0,0,canvas.width,canvas.height);
        // Ensure all registered shaders compiled once WebGL is available
        if(Object.keys(this._shaderPrograms).length === 0){
            this._buildAllShaderPrograms();
        }
        const progInfo = this._shaderPrograms[this._activeShaderKey] || this._shaderPrograms['PX'];
        const program = progInfo ? progInfo.program : glRes.basicProgram;
        gl.useProgram(program);
        gl.bindBuffer(gl.ARRAY_BUFFER, glRes.vbo);
        const aPos = progInfo ? progInfo.aPos : glRes.aPosBasic;
        const aTex = progInfo ? progInfo.aTex : glRes.aTexBasic;
        gl.enableVertexAttribArray(aPos);
        gl.vertexAttribPointer(aPos, 2, gl.FLOAT, false, 16, 0);
        gl.enableVertexAttribArray(aTex);
        gl.vertexAttribPointer(aTex, 2, gl.FLOAT, false, 16, 8);

        // --- Bind uniforms ---
        if (progInfo) {
            if (progInfo.uTime) gl.uniform1f(progInfo.uTime, performance.now()/1000.0);
            if (progInfo.uTexSize) gl.uniform2f(progInfo.uTexSize, 256.0, 240.0);
            if (progInfo.uStrength) gl.uniform1f(progInfo.uStrength, this._rfStrength);
            if (progInfo.options && typeof progInfo.options.onBind === 'function') {
                try { progInfo.options.onBind(gl, progInfo, this); } catch(e){ /* ignore */ }
            }
        }

        // --- Bind previous frame texture for MSH shader ---
        if (isMSH && progInfo) {
            // uPrevTex is always texture unit 1
            const uPrevTexLoc = gl.getUniformLocation(program, 'uPrevTex');
            if (uPrevTexLoc && glRes.prevFrameTexture) {
                gl.activeTexture(gl.TEXTURE1);
                gl.bindTexture(gl.TEXTURE_2D, glRes.prevFrameTexture);
                gl.uniform1i(uPrevTexLoc, 1);
                gl.activeTexture(gl.TEXTURE0); // restore default
            }
        }

        // --- Draw to screen ---
        gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);

        // --- After drawing, update previous frame texture (only for MSH) ---
        if (isMSH && glRes.prevFrameFBO && glRes.prevFrameTexture) {
            gl.bindFramebuffer(gl.FRAMEBUFFER, glRes.prevFrameFBO);
            gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, glRes.prevFrameTexture, 0);
            // Copy the current NES texture to prevFrameTexture
            gl.bindTexture(gl.TEXTURE_2D, glRes.texture);
            gl.viewport(0,0,256,240);
            // Use a simple passthrough shader to blit
            const pxProg = this._shaderPrograms['PX']?.program || glRes.basicProgram;
            gl.useProgram(pxProg);
            gl.bindBuffer(gl.ARRAY_BUFFER, glRes.vbo);
            const aPos = this._shaderPrograms['PX']?.aPos || glRes.aPosBasic;
            const aTex = this._shaderPrograms['PX']?.aTex || glRes.aTexBasic;
            gl.enableVertexAttribArray(aPos);
            gl.vertexAttribPointer(aPos, 2, gl.FLOAT, false, 16, 0);
            gl.enableVertexAttribArray(aTex);
            gl.vertexAttribPointer(aTex, 2, gl.FLOAT, false, 16, 8);
            gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            gl.bindFramebuffer(gl.FRAMEBUFFER, null);
            gl.viewport(0,0,canvas.width,canvas.height);
        }
    },
    // Zero-copy framebuffer support removed (HOTPOT-05 rollback). Using legacy marshalled framebuffer path only.
    // Retained generic query flag helper (used by other feature gates like Audio SAB)
    hasQueryFlag: function(flag){
        try { const u=new URL(window.location.href); const v=u.searchParams.get(flag); return v==="1"||v==="true"||v==="yes"; } catch { return false; }
    },
    // (toggleShader kept above for backwards compat via new implementation)

    setRfStrength: function (strength) {
        const s = Math.max(0.2, Math.min(strength, 3.0));
        this._rfStrength = s;
        if (this._glObjects && this._gl) {
            this._gl.useProgram(this._glObjects.program);
            if (this._glObjects.uStrength) this._gl.uniform1f(this._glObjects.uStrength, s);
        }
        return s;
    },

    _initWebGL: function (canvas) {
        // If already initialized for this canvas (programs created) just return
        if (this._gl && this._gl.canvas === canvas && this._glObjects && this._glObjects.basicProgram) return true;
        try {
            const gl = canvas.getContext('webgl', { premultipliedAlpha:false }) || canvas.getContext('experimental-webgl');
            if (!gl) return false;
            this._gl = gl;
            // Try enabling standard derivatives for shaders that use dFdx/dFdy (WebGL1)
            try {
                this._oesDerivatives = gl.getExtension('OES_standard_derivatives') || null;
                if (this._oesDerivatives) { console.log('[NES] OES_standard_derivatives enabled'); }
            } catch { this._oesDerivatives = null; }
            // Ensure built-in shaders registered before compile
            this._registerBuiltInShaders();
            const vs=this._compile(gl, gl.VERTEX_SHADER, this._sharedVertexSource);
            if(!vs){ console.warn('Vertex shader failed to compile'); return false; }
            // We'll lazily compile fragment shaders later, but need a basic program for fallback
            const basicFrag = `precision mediump float;varying vec2 vTex;uniform sampler2D uTex;void main(){vec2 uv=vec2(vTex.x,1.0-vTex.y);gl_FragColor=texture2D(uTex,uv);}`;
            const fsBasic=this._compile(gl, gl.FRAGMENT_SHADER, basicFrag);
            const basicProgram=gl.createProgram();
            gl.attachShader(basicProgram,vs);gl.attachShader(basicProgram,fsBasic);gl.linkProgram(basicProgram);
            if(!gl.getProgramParameter(basicProgram,gl.LINK_STATUS)){console.warn('Basic shader link fail',gl.getProgramInfoLog(basicProgram));return false;}
            const vbo=gl.createBuffer();
            gl.bindBuffer(gl.ARRAY_BUFFER,vbo);
            // x,y,u,v for 4 vertices (triangle strip)
            const data=new Float32Array([
                -1,-1, 0,0,
                 1,-1, 1,0,
                -1, 1, 0,1,
                 1, 1, 1,1
            ]);
            gl.bufferData(gl.ARRAY_BUFFER,data,gl.STATIC_DRAW);
            const tex=gl.createTexture();
            gl.bindTexture(gl.TEXTURE_2D,tex);
            gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MIN_FILTER,gl.NEAREST);
            gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MAG_FILTER,gl.NEAREST);
            gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_S,gl.CLAMP_TO_EDGE);
            gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_T,gl.CLAMP_TO_EDGE);
            this._glObjects={
                basicProgram:basicProgram,
                vbo:vbo,
                texture:tex,
                aPosBasic:gl.getAttribLocation(basicProgram,'aPos'),
                aTexBasic:gl.getAttribLocation(basicProgram,'aTex'),
                initialized:false
            };
            // Build all shader programs now (could defer until first use)
            this._buildAllShaderPrograms(vs);
            if(!this._basicLogged){ console.log('[NES] Shader system initialized'); this._basicLogged=true; }
            return true;
        } catch(e) {
            console.warn('WebGL init failed, falling back to 2D', e);
            this._gl=null; this._glObjects=null; return false;
        }
    },

    _registerBuiltInShaders(){
        if(Object.keys(this._shaderRegistry).length>0) return; // already registered via C# bridge
        // Always ensure a safe passthrough exists.
        this.registerShader('PX', 'PX', `precision mediump float;varying vec2 vTex;uniform sampler2D uTex;void main(){vec2 uv=vec2(vTex.x,1.0-vTex.y);gl_FragColor=texture2D(uTex,uv);}`);
    },

    _buildAllShaderPrograms(vs){
        if(!this._gl) return;
        const gl=this._gl;
        if(!vs){ vs=this._compile(gl, gl.VERTEX_SHADER, this._sharedVertexSource); if(!vs) return; }
        for(const key of Object.keys(this._shaderRegistry)){
            if(this._shaderPrograms[key]) continue; // already built
            let fragSrc = this._shaderRegistry[key].fragment;
            // Inject a tiny prelude: enable extension and define HAS_DERIVATIVES when supported.
            // Guard the define to avoid redefinition warnings if the shader sets it.
            const hasDerivatives = !!this._oesDerivatives;
            const prelude = hasDerivatives
                ? `#extension GL_OES_standard_derivatives : enable\n#ifndef HAS_DERIVATIVES\n#define HAS_DERIVATIVES 1\n#endif\n`
                : `#ifndef HAS_DERIVATIVES\n#define HAS_DERIVATIVES 0\n#endif\n`;
            fragSrc = prelude + fragSrc;
            const fs = this._compile(gl, gl.FRAGMENT_SHADER, fragSrc);
            if(!fs){ console.warn('Fragment compile failed for', key); continue; }
            const prog = gl.createProgram();
            gl.attachShader(prog, vs); gl.attachShader(prog, fs); gl.linkProgram(prog);
            if(!gl.getProgramParameter(prog, gl.LINK_STATUS)){
                console.warn('Shader link fail', key, gl.getProgramInfoLog(prog));
                continue;
            }
            const info={
                key,
                program:prog,
                aPos:gl.getAttribLocation(prog,'aPos'),
                aTex:gl.getAttribLocation(prog,'aTex'),
                uTime:gl.getUniformLocation(prog,'uTime'),
                uTexSize:gl.getUniformLocation(prog,'uTexSize'),
                uStrength:gl.getUniformLocation(prog,'uStrength'),
                options:this._shaderRegistry[key].options||{}
            };
            gl.useProgram(prog);
            const uTexLoc = gl.getUniformLocation(prog,'uTex'); if(uTexLoc) gl.uniform1i(uTexLoc,0);
            if(info.uStrength) gl.uniform1f(info.uStrength, this._rfStrength);
            this._shaderPrograms[key]=info;
            if(!this._rfLogged && key==='RF'){ console.log('[NES] RF composite shader active'); this._rfLogged=true; }
        }
        // Ensure active key valid
        if(!this._shaderPrograms[this._activeShaderKey]){
            const keys=Object.keys(this._shaderPrograms); if(keys.length>0) this._activeShaderKey=keys[0];
        }
    },

    _compile: function(gl,type,src){
        const s=gl.createShader(type);
        gl.shaderSource(s,src);
        gl.compileShader(s);
        if(!gl.getShaderParameter(s,gl.COMPILE_STATUS)){
            console.warn('Shader compile error', gl.getShaderInfoLog(s));
            return null;
        }
        return s;
    },

    startEmulationLoop: function (dotNetRef) {
        // Always set the latest .NET ref
        this._dotNetRef = dotNetRef;
        // Proactively cancel any orphan rAF to ensure single producer
        if (this._rafId != null) {
            try { cancelAnimationFrame(this._rafId); } catch {}
            this._rafId = null;
        }
        if (this._loopActive) return; // idempotent start
        this._loopActive = true;
        const step = () => {
            if (!this._loopActive) return;
            if (this._dotNetRef) {
                // Fire and forget; timing not awaited to avoid jank
                try { this._dotNetRef.invokeMethodAsync('FrameTick'); } catch {}
            }
            this._rafId = requestAnimationFrame(step);
        };
        this._rafId = requestAnimationFrame(step);
    },

    stopEmulationLoop: function () {
        // Flip flag first so any in-flight step sees false
        this._loopActive = false;
        // Cancel pending animation frame if any
        if (this._rafId != null) {
            try { cancelAnimationFrame(this._rafId); } catch {}
            this._rafId = null;
        }
        // Clear reference to avoid stray calls on stale refs
        this._dotNetRef = null;
    },

    registerInput: function (dotNetRef) {
        if (!dotNetRef) {
            console.error('dotNetRef is null');
            return;
        }
        
        window.nesInputState = new Array(8).fill(false);
        
        const updateInput = (changed) => {
            if (changed) {
                try {
                    dotNetRef.invokeMethodAsync('UpdateInput', window.nesInputState);
                } catch (error) {
                    console.error('Error invoking UpdateInput:', error);
                }
            }
        };

        document.addEventListener('keydown', function (e) {
            const active = document.activeElement;
            if (active && (active.tagName === 'INPUT' || active.tagName === 'TEXTAREA' || active.isContentEditable)) {
                // Allow regular typing when focusing an input field (e.g., ROM search)
                return;
            }
            let changed = false;
            switch (e.code) {
                case 'ArrowUp': 
                    e.preventDefault();
                    window.nesInputState[0] = true; 
                    changed = true; 
                    break;
                case 'ArrowDown': 
                    e.preventDefault();
                    window.nesInputState[1] = true; 
                    changed = true; 
                    break;
                case 'ArrowLeft': 
                    e.preventDefault();
                    window.nesInputState[2] = true; 
                    changed = true; 
                    break;
                case 'ArrowRight': 
                    e.preventDefault();
                    window.nesInputState[3] = true; 
                    changed = true; 
                    break;
                case 'KeyZ': 
                    e.preventDefault();
                    window.nesInputState[5] = true; 
                    changed = true; 
                    break; // B
                case 'KeyX': 
                    e.preventDefault();
                    window.nesInputState[4] = true; 
                    changed = true; 
                    break; // A
                case 'Space': 
                    e.preventDefault();
                    window.nesInputState[6] = true; 
                    changed = true; 
                    break; // Select
                case 'Enter': 
                    e.preventDefault();
                    window.nesInputState[7] = true; 
                    changed = true; 
                    break; // Start
            }
            updateInput(changed);
        });

        document.addEventListener('keyup', function (e) {
            const active = document.activeElement;
            if (active && (active.tagName === 'INPUT' || active.tagName === 'TEXTAREA' || active.isContentEditable)) {
                return;
            }
            let changed = false;
            switch (e.code) {
                case 'ArrowUp': 
                    window.nesInputState[0] = false; 
                    changed = true; 
                    break;
                case 'ArrowDown': 
                    window.nesInputState[1] = false; 
                    changed = true; 
                    break;
                case 'ArrowLeft': 
                    window.nesInputState[2] = false; 
                    changed = true; 
                    break;
                case 'ArrowRight': 
                    window.nesInputState[3] = false; 
                    changed = true; 
                    break;
                case 'KeyZ': 
                    window.nesInputState[5] = false; 
                    changed = true; 
                    break;
                case 'KeyX': 
                    window.nesInputState[4] = false; 
                    changed = true; 
                    break;
                case 'Space': 
                    window.nesInputState[6] = false; 
                    changed = true; 
                    break;
                case 'Enter': 
                    window.nesInputState[7] = false; 
                    changed = true; 
                    break;
            }
            updateInput(changed);
        });
    },

    playAudio: function (audioBuffer, sampleRate) {
        try {
            if (!window.nesAudioCtx) {
                window.nesAudioCtx = new (window.AudioContext || window.webkitAudioContext)();
            }
            
            const ctx = window.nesAudioCtx;
            
            // Resume context if it's suspended (required for Chrome autoplay policy)
            if (ctx.state === 'suspended') {
                ctx.resume();
            }
            
            if (!audioBuffer || audioBuffer.length === 0) {
                return; // No audio data to play
            }
            
            const buffer = ctx.createBuffer(1, audioBuffer.length, sampleRate);
            const channel = buffer.getChannelData(0);
            
            for (let i = 0; i < audioBuffer.length; i++) {
                channel[i] = audioBuffer[i];
            }
            
            const source = ctx.createBufferSource();
            source.buffer = buffer;
            source.connect(ctx.destination);
            // Maintain a running scheduled time to ensure continuous playback
            if (!window._nesAudioTimeline) {
                window._nesAudioTimeline = ctx.currentTime + 0.02; // small initial lead
            }
            // If timeline fell behind currentTime, reset with small lead to avoid large jumps
            if (window._nesAudioTimeline < ctx.currentTime) {
                window._nesAudioTimeline = ctx.currentTime + 0.01;
            }
            const when = window._nesAudioTimeline;
            source.start(when);
            window._nesAudioTimeline += buffer.duration;
        } catch (error) {
            console.warn('Audio playback error:', error);
        }
    }
    ,
    // ==== SoundFont note event bridge (APU_WF / APU_MNES SoundFontMode) ====
    noteEvent: function(channel, program, midiNote, velocity, on){
        try {
            const active = window._nesActiveSoundFontCore; // undefined => compatibility dual dispatch
            const layering = !!window._nesAllowLayering;
            const wantMnes = !active || active === 'MNES';
            const wantWf = !active || active === 'WF';
            // WF path
            if(window.nesSoundFont){
                if(wantWf){
                    if(on){ window.nesSoundFont.enable && window.nesSoundFont.enable(); }
                    window.nesSoundFont.handleNote && window.nesSoundFont.handleNote(channel, program, midiNote, velocity, !!on);
                    if(active === 'WF' || (!active && layering)){ this._sfCounters.wf++; }
                } else {
                    this._sfCounters.suppressedWf++;
                }
            }
            // MNES path
            if(window.mnesSf2){
                if(wantMnes){
                    if(on){ window.mnesSf2.enable && window.mnesSf2.enable(); }
                    window.mnesSf2.handleNote && window.mnesSf2.handleNote(channel, program, midiNote, velocity, !!on);
                    if(active === 'MNES' || (!active && layering)){ this._sfCounters.mnes++; }
                } else {
                    this._sfCounters.suppressedMnes++;
                }
            }
            // Warning if suppressed events accumulate (indicates potential mis-set active core)
            if(active){
                const { suppressedMnes, suppressedWf } = this._sfCounters;
                if(active === 'MNES' && suppressedWf >= this._sfWarnThreshold){
                    console.warn('[NES] WF events suppressed while MNES active (', suppressedWf, ')');
                    this._sfCounters.suppressedWf = 0; // avoid log spam
                }
                if(active === 'WF' && suppressedMnes >= this._sfWarnThreshold){
                    console.warn('[NES] MNES events suppressed while WF active (', suppressedMnes, ')');
                    this._sfCounters.suppressedMnes = 0;
                }
            }
        } catch(e){ console.warn('noteEvent failed', e); }
    }
    ,
    flushSoundFont: function(){
        try {
            if(window.nesSoundFont){
                window.nesSoundFont.disable && window.nesSoundFont.disable();
            }
            if(window.mnesSf2){
                window.mnesSf2.disable && window.mnesSf2.disable();
            }
        } catch(e){ console.warn('flushSoundFont failed', e); }
    }
    ,
    // === SoundFont active core management & telemetry ===
    // Global flag (also duplicated on window for defensive guards inside individual synth scripts)
    _sfCounters: { mnes:0, wf:0, suppressedMnes:0, suppressedWf:0 },
    _sfWarnThreshold: 3,
    setActiveSoundFontCore: function(coreId, options){
        // coreId: 'MNES' | 'WF' | null/undefined (dual dispatch compatibility mode)
        const prev = window._nesActiveSoundFontCore;
        if(coreId !== 'MNES' && coreId !== 'WF'){ coreId = undefined; }
        const now = performance.now();
        // Micro-throttle: ignore duplicate set within 900ms to reduce churn (unless options.force)
        if(prev === coreId){
            if(!this._sfLastSetCoreTs) this._sfLastSetCoreTs = 0;
            if(!options || !options.force){
                if(now - this._sfLastSetCoreTs < 900){
                    if(window._nesSfDevLogging){ console.log('[SF] setActiveSoundFontCore duplicate ignored:', coreId||'(compat)'); }
                    return coreId || '';
                }
            }
        }
        this._sfLastSetCoreTs = now;
        // Active time accounting: close out prev core time span
        if(!this._sfActiveTimeMs){ this._sfActiveTimeMs = { mnes:0, wf:0 }; }
        if(!this._sfActiveStart){ this._sfActiveStart = { mnes:0, wf:0 }; }
        const nowTs = performance.now();
        if(prev){
            if(prev === 'MNES' && this._sfActiveStart.mnes){ this._sfActiveTimeMs.mnes += (nowTs - this._sfActiveStart.mnes); this._sfActiveStart.mnes = 0; }
            if(prev === 'WF' && this._sfActiveStart.wf){ this._sfActiveTimeMs.wf += (nowTs - this._sfActiveStart.wf); this._sfActiveStart.wf = 0; }
        }
        // Disable previous core explicitly when switching (unless layering allowed and core unset)
        if(prev && coreId && prev !== coreId){
            try {
                if(prev === 'MNES' && window.mnesSf2){ window.mnesSf2.disable && window.mnesSf2.disable(); }
                if(prev === 'WF' && window.nesSoundFont){ window.nesSoundFont.disable && window.nesSoundFont.disable(); }
            } catch{}
        }
        window._nesActiveSoundFontCore = coreId; // public global
    if(window._nesSfDevLogging){ console.log('[SF] Core change', prev||'(none)','=>', coreId||'(compat)', 'flush=', options?.flush !== false, 'eager=', options?.eager !== false); }
        // Optionally enable new core eagerly (default true)
        const eager = options?.eager !== false;
        if(eager && coreId){
            try {
                if(coreId === 'MNES' && window.mnesSf2){ window.mnesSf2.enable && window.mnesSf2.enable(); }
                if(coreId === 'WF' && window.nesSoundFont){ window.nesSoundFont.enable && window.nesSoundFont.enable(); }
            } catch{}
        }
        // Flush inactive synth to avoid linger tails when switching (optional)
        if(options?.flush !== false && prev && prev !== coreId){
            try { this.flushSoundFont(); } catch{}
            // Re-enable chosen core if flush disabled it
            if(coreId){
                try {
                    if(coreId === 'MNES' && window.mnesSf2){ window.mnesSf2.enable && window.mnesSf2.enable(); }
                    if(coreId === 'WF' && window.nesSoundFont){ window.nesSoundFont.enable && window.nesSoundFont.enable(); }
                } catch{}
            }
        }
        // Start timing for new active core
        if(coreId === 'MNES'){ this._sfActiveStart.mnes = performance.now(); }
        if(coreId === 'WF'){ this._sfActiveStart.wf = performance.now(); }
        return coreId || '';
    },
    getActiveSoundFontCore(){ return window._nesActiveSoundFontCore || ''; },
    setSoundFontLayering(on){ window._nesAllowLayering = !!on; return window._nesAllowLayering; },
    debugReport(){
        const nowTs = performance.now();
        if(this._sfActiveStart){
            if(this._sfActiveStart.mnes){
                if(!this._sfActiveTimeMs) this._sfActiveTimeMs = { mnes:0, wf:0 };
                // Add a non-persisted live delta for report only
            }
        }
        const live = { mnes:0, wf:0 };
        if(this._sfActiveTimeMs){ live.mnes = this._sfActiveTimeMs.mnes; live.wf = this._sfActiveTimeMs.wf; }
        if(this._sfActiveStart){
            if(this._sfActiveStart.mnes){ live.mnes += (nowTs - this._sfActiveStart.mnes); }
            if(this._sfActiveStart.wf){ live.wf += (nowTs - this._sfActiveStart.wf); }
        }
        return {
            activeCore: window._nesActiveSoundFontCore || null,
            layering: !!window._nesAllowLayering,
            counters: { ...this._sfCounters },
            activeTimeMs: live,
            audioLeadMs: this.getAudioLeadMs ? this.getAudioLeadMs() : null
        };
    },
    resetSoundFontCounters(){ this._sfCounters = { mnes:0, wf:0, suppressedMnes:0, suppressedWf:0 }; },
    // === Dev diagnostics (ring buffer lead + overlay) ===
    getAudioLeadMs(){
        try {
            if(!window.nesAudioCtx || !window._nesAudioTimeline) return 0;
            const ctx = window.nesAudioCtx;
            const lead = (window._nesAudioTimeline - ctx.currentTime) * 1000.0;
            return Math.max(0, lead);
        } catch { return 0; }
    },
    _sfDiagTimer:null,
    _sfDiagOverlay:null,
    enableSoundFontDevLogging(on){ window._nesSfDevLogging = !!on; return window._nesSfDevLogging; },
    startSoundFontAudioOverlay(){
        if(this._sfDiagOverlay) return true; // already
        try {
            const div = document.createElement('div');
            div.id = 'sf-audio-overlay';
            div.style.position='fixed';
            div.style.bottom='4px';
            div.style.right='4px';
            div.style.font='11px monospace';
            div.style.background='rgba(0,0,0,0.55)';
            div.style.color='#fff';
            div.style.padding='4px 6px';
            div.style.borderRadius='4px';
            div.style.zIndex='9999';
            div.style.pointerEvents='none';
            div.textContent='SF diag…';
            document.body.appendChild(div);
            this._sfDiagOverlay = div;
            const update = ()=>{
                if(!this._sfDiagOverlay){ return; }
                const rep = this.debugReport();
                const lead = this.getAudioLeadMs();
                const core = rep.activeCore || 'DUAL';
                const m = Math.round(rep.counters.mnes);
                const w = Math.round(rep.counters.wf);
                let color = '#6cf';
                if(lead < 25) color = '#f66'; else if(lead < 45) color = '#fc6'; else if(lead > 120) color = '#6f6';
                this._sfDiagOverlay.style.border = '1px solid '+color;
                this._sfDiagOverlay.textContent = `SF ${core} L=${lead.toFixed(0)}ms M:${m} W:${w}`;
            };
            this._sfDiagTimer = setInterval(update, 500);
            update();
            return true;
        } catch(e){ console.warn('startSoundFontAudioOverlay failed', e); return false; }
    },
    stopSoundFontAudioOverlay(){
        try { if(this._sfDiagTimer){ clearInterval(this._sfDiagTimer); this._sfDiagTimer=null; } } catch{}
        if(this._sfDiagOverlay){ try { this._sfDiagOverlay.remove(); } catch{} this._sfDiagOverlay=null; }
    },
    readSelectedRoms: async function (inputElement) {
        try {
            const el = inputElement instanceof Element ? inputElement : (inputElement && inputElement.id ? document.getElementById(inputElement.id) : null);
            const files = el && el.files ? Array.from(el.files) : [];
            const results = [];
            for (const f of files) {
                if (!f.name.toLowerCase().endsWith('.nes')) continue;
                const data = await f.arrayBuffer();
                // size guard (4MB)
                if (data.byteLength > 4 * 1024 * 1024) continue;
                const bytes = new Uint8Array(data);
                let binary = '';
                for (let i = 0; i < bytes.length; i++) binary += String.fromCharCode(bytes[i]);
                const base64 = btoa(binary);
                results.push({ name: f.name, base64 });
                // Persist in IndexedDB for later session recall (simple cache)
                try { await this.saveRom(f.name, base64); } catch { /* ignore */ }
            }
            return results;
        } catch (e) {
            console.warn('readSelectedRoms error', e);
            return [];
        }
    },
    // ===== Migration from legacy localStorage (one-time) =====
    async migrateLocalStorageRoms(){
        try {
            // Only run if localStorage exists and there are rom_ keys
            if (!window.localStorage) return;
            const keys = [];
            for (let i=0;i<localStorage.length;i++){
                const k = localStorage.key(i);
                if (k && k.startsWith('rom_')) keys.push(k);
            }
            if (!keys.length) return;
            // For each, move to IDB if not already there
            const current = await this.getStoredRoms();
            const existingNames = new Set(current.map(r=>r.name));
            for (const k of keys){
                const base64 = localStorage.getItem(k);
                if (!base64) continue;
                const name = k.substring(4);
                if (!existingNames.has(name)){
                    await this.saveRom(name, base64);
                }
                try { localStorage.removeItem(k); } catch {}
            }
            console.log('[NES] Migrated', keys.length, 'legacy ROM(s) from localStorage to IndexedDB');
        } catch (e){ console.warn('ROM migration failed', e); }
    },
    initRomDragDrop: function(elementId, dotNetRef){
        const el = document.getElementById(elementId);
        if (!el) return;
        const highlight = (on)=>{ el.classList.toggle('drag-over', !!on); };
        ['dragenter','dragover'].forEach(evt=>el.addEventListener(evt, e=>{ e.preventDefault(); e.stopPropagation(); highlight(true);}));
        ['dragleave','drop'].forEach(evt=>el.addEventListener(evt, e=>{ e.preventDefault(); e.stopPropagation(); if(evt==='dragleave') highlight(false);}));
        el.addEventListener('drop', async (e)=>{
            highlight(false);
            const dt = e.dataTransfer;
            if (!dt || !dt.files) return;
            const files = Array.from(dt.files).filter(f=>f.name.toLowerCase().endsWith('.nes'));
            const results=[];
            for (const f of files){
                try {
                    const buf = await f.arrayBuffer();
                    if (buf.byteLength>4*1024*1024) continue;
                    const bytes = new Uint8Array(buf);
                    let binary='';
                    for(let i=0;i<bytes.length;i++) binary+=String.fromCharCode(bytes[i]);
                    const base64=btoa(binary);
                    results.push({name:f.name, base64});
                    try { await window.nesInterop.saveRom(f.name, base64); } catch {}
                } catch {}
            }
            if (results.length && dotNetRef){
                try { dotNetRef.invokeMethodAsync('OnRomsDropped', results); } catch(e){ console.warn('OnRomsDropped invoke fail', e);}        
            }
        });
    },

    // ===== Fullscreen + scaling support =====
    setMainRef(dotNetRef){ this._mainRef = dotNetRef; },
    _isFullscreen:false,
    _mobileBar:null,
    _fsWasPortrait:false,
    _origShellClasses:null,
    _prevScaleClass:null,
    _applyMobileFullscreen(on){
        const rootShell = document.getElementById('screen-shell');
        if(!rootShell) return;
        const portraitQuery = '(max-width: 899px) and (orientation: portrait)';
        const stillPortrait = window.matchMedia(portraitQuery).matches;
        if(on && !stillPortrait){
            // Prevent accidental activation if orientation changed between toggle & application
            return;
        }
        if(on){
            rootShell.classList.add('mobile-fs-active');
            document.documentElement.classList.add('mobile-no-scroll');
            document.body.classList.add('mobile-no-scroll');
            // Bottom bar now part of Razor markup; just wire events once
            if(!this._viewBar){
                const bar = document.getElementById('mobile-fs-view-bar');
                if(bar){
                    this._viewBar = bar;
                    bar.addEventListener('click', (e)=>{
                        const btn = e.target.closest('button[data-view]'); if(!btn) return;
                        const view = btn.getAttribute('data-view');
                        bar.querySelectorAll('button').forEach(b=>b.classList.toggle('active', b===btn));
                        if(this._mainRef){ try{ this._mainRef.invokeMethodAsync('JsSetMobileFsView', view); }catch{} }
                        this._syncMobileViews(view);
                    });
                }
            }
                        // controller markup now rendered server-side; init only when requested elsewhere
            // initial view sync (CSS controls visibility)
            this._syncMobileViews('controller');
            // Ensure touch controller is bound after the DOM is in its fullscreen layout
            try { this.initTouchController('touch-controller'); } catch {}
        } else {
            rootShell.classList.remove('mobile-fs-active');
            document.documentElement.classList.remove('mobile-no-scroll');
            document.body.classList.remove('mobile-no-scroll');
            if(this._mobileBar){ this._mobileBar.remove(); this._mobileBar=null; }
            if(this._viewBar){
                // Do not remove; it's part of markup. Just deactivate buttons state.
                this._viewBar.querySelectorAll('button').forEach(b=>b.classList.remove('active'));
                this._viewBar=null; // will be re-hooked next enter
            }
                        if(this._touchCtl){ /* controller lives in Razor; just detach handlers */ this._touchCtl=null; }
            // CSS hides bar outside fullscreen portrait
        }
    },
    _syncMobileViews(active){
        // All views (including controller) now reside inside .mobile-fs-extra-views via Razor conditional rendering.
        // Nothing to toggle at container level; controller visibility driven by Blazor diff.
        // Kept for compatibility with existing calls.
    },
    syncMobileView(view){
        // Called from .NET when Razor buttons clicked
        if(this._viewBar){
            this._viewBar.querySelectorAll('button').forEach(b=>{
                const v = b.getAttribute('data-view');
                b.classList.toggle('active', v===view);
            });
        }
        this._syncMobileViews(view);
    },
    initTouchController(containerId){
        try {
            const ctl = document.getElementById(containerId);
            if(!ctl) return;
            // Avoid re-binding
            if(ctl._nesBound) return; ctl._nesBound = true;
            this._touchCtl = ctl;
            const map = { up:0, down:1, left:2, right:3, a:4, b:5, select:6, start:7 };
            const activeTouches = new Map();
            const updateBtnVisual = (btnEl, pressed)=>{ if(!btnEl) return; btnEl.classList.toggle('pressed', !!pressed); };
            const setState = (btnKey, val)=>{ const idx = map[btnKey]; if(typeof idx!== 'number') return; if(!window.nesInputState) window.nesInputState=new Array(8).fill(false); window.nesInputState[idx]=val; if(this._mainRef) try{ this._mainRef.invokeMethodAsync('UpdateInput', window.nesInputState);}catch{} };
            const elementFromTouch = (touch)=>{ const el = document.elementFromPoint(touch.clientX, touch.clientY); if(!el) return null; return el.closest && el.closest('[data-btn]'); };
            const beginTouch = (touch)=>{ const target = elementFromTouch(touch); if(!target) return; const key = target.dataset.btn; if(!key) return; activeTouches.set(touch.identifier,key); setState(key,true); updateBtnVisual(target,true); };
            const moveTouch = (touch)=>{ const prevKey = activeTouches.get(touch.identifier); const currentEl = elementFromTouch(touch); const newKey = currentEl?.dataset.btn; if(prevKey && prevKey!==newKey){ setState(prevKey,false); const prevEl = ctl.querySelector(`[data-btn="${prevKey}"]`); updateBtnVisual(prevEl,false); activeTouches.delete(touch.identifier);} if(newKey && newKey!==prevKey){ activeTouches.set(touch.identifier,newKey); setState(newKey,true); updateBtnVisual(currentEl,true);} };
            const endTouch = (touch)=>{ const key = activeTouches.get(touch.identifier); if(key){ setState(key,false); const el = ctl.querySelector(`[data-btn="${key}"]`); updateBtnVisual(el,false); activeTouches.delete(touch.identifier);} };
            const invokeSystemAction = (act)=>{ switch(act){ case 'exit': this.toggleFullscreen(); break; case 'load': if(this._mainRef) try{ this._mainRef.invokeMethodAsync('JsLoadState'); }catch{} break; case 'save': if(this._mainRef) try{ this._mainRef.invokeMethodAsync('JsSaveState'); }catch{} break; } };
            ctl.addEventListener('touchstart',(e)=>{ e.preventDefault(); const actEl = e.target.closest('[data-act]'); if(actEl){ invokeSystemAction(actEl.dataset.act); return; } for(const t of e.changedTouches) beginTouch(t); }, {passive:false});
            ctl.addEventListener('touchmove',(e)=>{ e.preventDefault(); for(const t of e.changedTouches) moveTouch(t); }, {passive:false});
            ctl.addEventListener('touchend',(e)=>{ e.preventDefault(); for(const t of e.changedTouches) endTouch(t); }, {passive:false});
            ctl.addEventListener('touchcancel',(e)=>{ for(const t of e.changedTouches) endTouch(t); });
            ctl.addEventListener('click',(e)=>{ const act = e.target.closest('[data-act]')?.dataset.act; if(!act) return; invokeSystemAction(act); });
        } catch(e){ console.warn('initTouchController error', e); }
    },
    toggleFullscreen(){
        const shell = document.getElementById('screen-shell');
        if(!shell) return this._isFullscreen;
        const isMobilePortrait = window.matchMedia('(max-width: 899px) and (orientation: portrait)').matches;
        if(!this._isFullscreen){
                        if(this._origShellClasses==null && shell){ this._origShellClasses = shell.className; }
            // Record and strip scale classes so fullscreen always maximizes
            if(shell.classList.contains('scale-50')){ this._prevScaleClass='scale-50'; shell.classList.remove('scale-50'); }
            else if(shell.classList.contains('scale-100')){ this._prevScaleClass='scale-100'; shell.classList.remove('scale-100'); }
            // entering
            if(!document.fullscreenElement && !isMobilePortrait){
                if(shell.requestFullscreen){ shell.requestFullscreen().catch(()=>{}); }
            }
            if(isMobilePortrait){
                this._applyMobileFullscreen(true);
                this._fsWasPortrait = true;
            } else {
                shell.classList.add('fs-desktop-active');
            }
            this._isFullscreen = true;
        } else {
            // exiting
            if(document.fullscreenElement){ document.exitFullscreen().catch(()=>{}); }
            shell.classList.remove('fs-desktop-active');
            if(this._fsWasPortrait){ this._applyMobileFullscreen(false); this._fsWasPortrait=false; }
            this._isFullscreen = false;
            if(this._mainRef){ try{ this._mainRef.invokeMethodAsync('JsExitFullscreen'); }catch{} }
            // ensure shell baseline classes restored
            shell.classList.remove('mobile-fs-active');
                        if(this._origShellClasses!=null){ shell.className = this._origShellClasses; }
                        // Restore previous scaling if still on page
                        if(this._prevScaleClass && !shell.classList.contains(this._prevScaleClass)){
                            shell.classList.add(this._prevScaleClass);
                        }
        }
        return this._isFullscreen;
    }
    ,
    focusCorruptorPanel(){
        try {
            const rom = document.getElementById('rom-manager-panel');
            const cor = document.getElementById('corruptor-panel');
            if (rom && rom.hasAttribute('open')) rom.removeAttribute('open');
            if (cor && !cor.hasAttribute('open')) cor.setAttribute('open','');
            // Optionally scroll into view if panels overflow
            if (cor) {
                cor.scrollIntoView({behavior:'smooth', block:'nearest'});
            }
        } catch(e){ console.warn('focusCorruptorPanel error', e); }
    }
    ,
    // ===== Layout fallback: inject minimal styles if scoped CSS bundle failed to load (rebrand cache issue) =====
    ensureLayoutStyles(){
        try {
            const test = document.querySelector('.nes-grid');
            if(!test) return;
            const cs = getComputedStyle(test);
            if(cs.display !== 'grid'){
                if(!document.getElementById('nes-fallback-styles')){
                    const style = document.createElement('style');
                    style.id = 'nes-fallback-styles';
                    style.textContent = `/* Injected fallback because BrokenNes.styles.css appears missing */\n.nes-grid{display:grid;gap:1rem;}@media (min-width:900px){.nes-grid{grid-template-columns:minmax(0,1fr) minmax(440px,560px);} .side-panels{position:sticky;top:0.75rem;align-self:start;max-height:calc(100dvh - 2rem);overflow:auto;}}@media (min-width:1300px){.nes-grid{grid-template-columns:minmax(0,1fr) minmax(520px,640px);}}`;
                    document.head.appendChild(style);
                    console.warn('[NES] Fallback layout styles applied (scoped CSS bundle likely not loaded).');
                }
            }
        } catch(e){ console.warn('ensureLayoutStyles error', e); }
    }
};

// Responsive guard: if user rotates or resizes such that portrait condition no longer holds while in mobile fullscreen, exit gracefully.
window.addEventListener('resize', ()=>{
    try {
        if(window.nesInterop && window.nesInterop._isFullscreen){
            const portrait = window.matchMedia('(max-width: 899px) and (orientation: portrait)').matches;
            if(!portrait && window.nesInterop._fsWasPortrait){
                // Leave mobile fullscreen and attempt desktop fullscreen if width allows
                window.nesInterop._applyMobileFullscreen(false);
                const shell = document.getElementById('screen-shell');
                if(shell){ shell.classList.remove('mobile-fs-active'); }
                window.nesInterop._fsWasPortrait=false;
                // If real fullscreen API active keep; else ensure buttons hidden
                // CSS handles hiding bar when not in portrait mobile fullscreen
            }
            if(portrait && !window.nesInterop._fsWasPortrait && !document.fullscreenElement){
                // We are in fullscreen state (tracked) but not portraying mobile-fs; re-enter mobile layout
                window.nesInterop._applyMobileFullscreen(true);
                window.nesInterop._fsWasPortrait=true;
            }
        } else {
            // Not fullscreen: ensure mobile bar hidden (avoids stray state)
            // CSS handles hiding bar when exiting fullscreen
        }
    } catch(e){ console.warn('resize handler err', e); }
});

// Global fullscreenchange listener to keep state in sync (e.g., ESC key)
document.addEventListener('fullscreenchange', ()=>{
    try {
        const shell = document.getElementById('screen-shell');
        const active = !!document.fullscreenElement;
        if(!active && window.nesInterop && window.nesInterop._isFullscreen){
            // Desktop exit
            if(shell){ shell.classList.remove('fs-desktop-active','mobile-fs-active');
                        if(window.nesInterop._origShellClasses!=null) shell.className = window.nesInterop._origShellClasses; 
                        if(window.nesInterop._prevScaleClass && !shell.classList.contains(window.nesInterop._prevScaleClass)) shell.classList.add(window.nesInterop._prevScaleClass);
                    }
            if(window.nesInterop._fsWasPortrait){ window.nesInterop._applyMobileFullscreen(false); window.nesInterop._fsWasPortrait=false; }
            window.nesInterop._isFullscreen=false;
            if(window.nesInterop._mainRef){ try{ window.nesInterop._mainRef.invokeMethodAsync('JsExitFullscreen'); }catch{} }
        }
    } catch(e){ console.warn('fullscreenchange handler error', e); }
});
