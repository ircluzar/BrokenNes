// mnesSf2.js (revised) – Isolated AudioWorklet-based SoundFont bridge avoiding Blazor WASM Module collisions.
// Strategy: Use js-synthesizer *worklet* build only. No global Module mutation; FluidSynth lives inside AudioWorkletGlobalScope.
// Public API kept: mnesSf2.enable(), .disable(), .handleNote(channel, program, midiNote, velocity, on)
// Channels: P1,P2,TRI,NOI mapped to MIDI channels 0..3. Noise gets ignored (or could be future custom noise synth).
(function(){
    if(window.mnesSf2) return;
    const api = {};
    window.mnesSf2 = api;

    const SF2_PATH = 'sf2player/MNES.sf2';
    // We'll dynamically compose a single AudioWorklet script (blob) containing:
    //  1. libfluidsynth-2.0.2.js (Emscripten build) which defines global `Module`
    //  2. A small line assigning AudioWorkletGlobalScope.wasmModule = Module
    //  3. js-synthesizer.worklet.min.js which registers the 'fluid-js' processor
    // Main thread separately loads js-synthesizer.min.js (v1.5.0) for wrapper classes.
    const MAIN_LIB = 'sf2player/js-synthesizer.min.js';
    const FLUID_LIB = 'sf2player/libfluidsynth-2.0.2.js';
    const WORKLET_LIB = 'sf2player/js-synthesizer.worklet.min.js';
    const CHANNEL_MAP = { P1:0, P2:1, TRI:2, DPCM:3 }; // NOI handled separately (white-noise burst); DPCM uses channel 3
    // Bank logic: Core SF2 layout uses bank 0 for standard APU voices and bank 128 (combined) for DPCM (program 0).
    // MIDI Bank number = MSB*128 + LSB. Thus bank 128 => MSB=1, LSB=0. We'll emit CC0=1 then (optionally) CC32=0.
    const BANK_COMBINED_DPCM = 128;
    const BANK_MSB_DPCM = 1; // since 1*128 + 0 = 128
    const BANK_LSB_DPCM = 0;

    let ctx;            // AudioContext
    let synth;          // AudioWorkletNodeSynthesizer instance
    let node;           // AudioWorkletNode
    let sfLoaded = false;
    let enabled = false;
    let enabling = false;
    let fatalInitError = false;
    let channelPrograms = new Array(16).fill(-1);
    let channelBanks = new Array(16).fill(-1); // combined bank numbers we believe are active
    let lastEnableAttempt = 0;
    const ENABLE_THROTTLE_MS = 1500;
    const WORKLET_BLOCK_SIZE = 128; // Lower = lower latency, higher CPU. Was 1024 (~21ms). 128 @48k ~2.7ms.
    // Track active notes so we can force release on disable (prevents lingering tails when switching cores)
    const activeNotes = new Map(); // channel(int) -> Set(midiNote)

    // Queue note/program events that arrive before SF2 fully loaded to prevent hearing FluidSynth's internal fallback tones.
    const pendingEvents = [];

    // Simple runtime options
    const options = {
        noiseChannel: true,          // set false to disable white-noise fallback for 'NOI'
        autoEnableOnNote: true,
    debug: false,
    dpcmFallbackProgram: null // set to a number (e.g. 5 for Noise) to override DPCM instrument if SF2 problematic
    };
    api.options = options;

    Object.defineProperty(api,'fatalInitError',{get:()=>fatalInitError});
    Object.defineProperty(api,'isWorklet',{get:()=>true});

    function ensureCtx(){
        if(!ctx){
            try {
                ctx = new (window.AudioContext||window.webkitAudioContext)({ latencyHint: 'interactive' });
            } catch {
                ctx = new (window.AudioContext||window.webkitAudioContext)();
            }
            if(!ctx.audioWorklet){
                console.warn('[MNES] AudioWorklet not supported; abandoning js-synthesizer path.');
                fatalInitError = true;
                return null;
            }
        }
        if(ctx.state==='suspended'){ try{ ctx.resume(); }catch{} }
        return ctx;
    }

    async function ensureMainLib(){
        if(window.JSSynth && window.JSSynth.AudioWorkletNodeSynthesizer) return true;
        if(ensureMainLib._p) return ensureMainLib._p;
        ensureMainLib._p = new Promise((resolve,reject)=>{
            // Provide a stub Emscripten Module so v1.5.0 main lib doesn't throw ReferenceError.
            if(!window.Module){
                window.Module = { calledRun: true }; // pretend already initialized (we only need wrapper classes)
            }
            const s = document.createElement('script');
            s.src = MAIN_LIB;
            s.async = true;
            s.onload = ()=>{
                if(window.JSSynth && window.JSSynth.AudioWorkletNodeSynthesizer){
                    resolve(true);
                } else {
                    console.warn('[MNES] js-synthesizer main lib loaded but API missing');
                    resolve(false);
                }
            };
            s.onerror = (e)=>{ console.warn('[MNES] Main js-synthesizer lib load failed', e); reject(e); };
            document.head.appendChild(s);
        }).catch(e=>{ fatalInitError = true; return false; });
        return ensureMainLib._p;
    }

    async function ensureWorkletLoaded(){
        if(!ctx) ensureCtx();
        if(!ctx) return false;
        const mainOk = await ensureMainLib();
        if(!mainOk) return false;
        if(ensureWorkletLoaded._p) return ensureWorkletLoaded._p;
        ensureWorkletLoaded._p = (async ()=>{
            // Fetch both libraries in parallel
            const [fluidText, workletText] = await Promise.all([
                fetch(FLUID_LIB).then(r=>{ if(!r.ok) throw new Error('HTTP '+r.status+' '+FLUID_LIB); return r.text(); }),
                fetch(WORKLET_LIB).then(r=>{ if(!r.ok) throw new Error('HTTP '+r.status+' '+WORKLET_LIB); return r.text(); })
            ]);
            const combined = `/* MNES combined worklet */\n`+
                fluidText + "\nAudioWorkletGlobalScope.wasmModule = Module;\n" +
                workletText + "\n/* end combined */";
            const blob = new Blob([combined], { type: 'application/javascript' });
            const url = URL.createObjectURL(blob);
            ensureWorkletLoaded._url = url; // keep for potential revocation later
            await ctx.audioWorklet.addModule(url);
            if(!window.JSSynth || !window.JSSynth.AudioWorkletNodeSynthesizer){
                console.warn('[MNES] JSSynth main facade not present after worklet registration');
                return false;
            }
            return true;
        })().catch(e=>{ console.warn('[MNES] Worklet composition failed', e); fatalInitError=true; return false; });
        return ensureWorkletLoaded._p;
    }

    async function ensureSynth(){
        if(synth) return synth;
        if(fatalInitError) return null;
        const ok = await ensureWorkletLoaded();
        if(!ok){ return null; }
        try {
            // js-synthesizer worklet exposes JSSynth globally after module load
            if(window.JSSynth && window.JSSynth.waitForReady){
                try { await window.JSSynth.waitForReady(); } catch{}
            }
            synth = new window.JSSynth.AudioWorkletNodeSynthesizer();
            node = synth.createAudioNode(ctx, WORKLET_BLOCK_SIZE);
            node.connect(ctx.destination);
            console.log('[MNES] Worklet synthesizer ready');
        } catch(e){
            console.warn('[MNES] Synth construct failed', e); fatalInitError = true; return null;
        }
        return synth;
    }

    function flushPending(){
        if(!synth || !sfLoaded || !pendingEvents.length) return;
        const events = pendingEvents.splice(0, pendingEvents.length);
        for(const ev of events){
            try { processEvent(ev); } catch{}
        }
    }

    async function ensureSoundFont(){
        if(sfLoaded) return true;
        const s = await ensureSynth();
        if(!s) return false;
        try {
            const resp = await fetch(SF2_PATH);
            if(!resp.ok) throw new Error('HTTP '+resp.status);
            const buf = await resp.arrayBuffer();
            await s.loadSFont(buf);
            sfLoaded = true;
            // Set initial programs 0 (bank 0) for standard voices; DPCM gets bank 128
            for(const [k,ch] of Object.entries(CHANNEL_MAP)){
                try {
                    if(k === 'DPCM'){
                        try { s.midiControlChange(ch,0,BANK_MSB_DPCM); channelBanks[ch]=BANK_MSB_DPCM*128; } catch{}
                        try { s.midiControlChange(ch,32,BANK_LSB_DPCM); } catch{}
                        try { s.midiProgramChange(ch,0); channelPrograms[ch]=0; } catch{}
                    } else {
                        s.midiProgramChange(ch,0); channelPrograms[ch]=0; channelBanks[ch]=0;
                    }
                } catch{}
            }
            console.log('[MNES] SoundFont loaded');
            flushPending();
            return true;
        } catch(e){ console.warn('[MNES] SoundFont load failed', e); return false; }
    }

    api.enable = async function(){
        if(enabled || fatalInitError || enabling) return;
        enabling = true;
        try {
            const s = await ensureSynth();
            if(!s) return;
            const ok = await ensureSoundFont();
            if(ok){ enabled = true; console.log('[MNES] Enabled (worklet)'); }
        } finally { enabling = false; }
    };

    api.disable = function(){
        enabled = false;
        // Send note-off for any tracked active notes to encourage quick tail release
        try {
            if(synth){
                for(const [ch,set] of activeNotes.entries()){
                    if(!set) continue;
                    for(const n of set){
                        try { synth.midiNoteOff(ch, n); } catch{}
                    }
                    set.clear();
                }
            }
        } catch{}
        if(window._nesSfDevLogging){ console.log('[MNES] Disabled (note offs sent)'); }
        // We keep node connected for reuse; could disconnect if desired
    };

    function processEvent(ev){
        const { channel, program, midiNote, velocity, on } = ev;
        if(channel === 'NOI'){
            if(!options.noiseChannel) return;
            // Simple noise burst fallback (not from FluidSynth)
            try {
                if(!on || velocity<=0) return; // one-shot only
                const c = ensureCtx(); if(!c) return;
                const dur = 0.07; // slightly shorter for tighter feel
                const buffer = c.createBuffer(1, Math.floor(c.sampleRate*dur), c.sampleRate);
                const data = buffer.getChannelData(0);
                const amp = (velocity/127) * 0.25;
                for(let i=0;i<data.length;i++){ data[i] = (Math.random()*2-1) * amp; }
                const src = c.createBufferSource(); src.buffer = buffer;
                const g = c.createGain();
                const t0 = c.currentTime;
                g.gain.setValueAtTime(1,t0);
                g.gain.exponentialRampToValueAtTime(0.0001,t0+dur);
                src.connect(g).connect(c.destination); src.start();
            } catch{}
            return;
        }
    const ch = CHANNEL_MAP[channel];
        if(ch == null) return;
        // Ensure bank selection for DPCM channel before program changes; optional fallback
        let effectiveProgram = program;
        if(channel === 'DPCM'){
            if(options.dpcmFallbackProgram != null){ effectiveProgram = options.dpcmFallbackProgram; }
            const desiredCombined = BANK_COMBINED_DPCM;
            if(channelBanks[ch] !== desiredCombined){
                try { synth.midiControlChange(ch,0,BANK_MSB_DPCM); channelBanks[ch]=BANK_MSB_DPCM*128; } catch{}
                try { synth.midiControlChange(ch,32,BANK_LSB_DPCM); } catch{}
            }
        }
        if(effectiveProgram != null && effectiveProgram>=0 && effectiveProgram<=127 && channelPrograms[ch]!==effectiveProgram){
            try { synth.midiProgramChange(ch, effectiveProgram); channelPrograms[ch]=effectiveProgram; if(options.debug) console.log('[MNES] prog ch', channel, effectiveProgram); } catch(e){ if(options.debug) console.log('[MNES] prog fail', e); }
        }
        try {
            if(on){
                if(velocity>0) synth.midiNoteOn(ch, midiNote, Math.min(127, Math.max(1, velocity)));
                // Track active note
                let set = activeNotes.get(ch); if(!set){ set = new Set(); activeNotes.set(ch,set); }
                set.add(midiNote);
            } else {
                synth.midiNoteOff(ch, midiNote);
                const set = activeNotes.get(ch); if(set){ set.delete(midiNote); }
            }
        } catch{}
    }

    api.handleNote = async function(channel, program, midiNote, velocity, on){
    // Defensive gating: if a different core is marked active and layering disabled, early return.
    if(window._nesActiveSoundFontCore && window._nesActiveSoundFontCore !== 'MNES' && !window._nesAllowLayering){ return; }
        if(fatalInitError) return;
        if(!enabled){
            if(on && options.autoEnableOnNote){
                const now = performance.now();
                if(now - lastEnableAttempt > ENABLE_THROTTLE_MS){
                    lastEnableAttempt = now;
                    try { await api.enable(); } catch{}
                }
            }
            if(!enabled) return;
        }
        const s = await ensureSynth();
        if(!s) return;
        // If soundfont not yet loaded, queue to avoid internal fallback timbre.
        if(!sfLoaded){
            pendingEvents.push({ channel, program, midiNote, velocity, on });
            if(options.debug) console.log('[MNES] queued event (sf loading)', pendingEvents.length);
            return;
        }
        processEvent({ channel, program, midiNote, velocity, on });
    };
})();
