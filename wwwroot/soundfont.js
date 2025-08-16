// soundfont.js - lightweight SoundFont/oscillator bridge for BrokenNes
// Keeps a single global audio context & instrument cache; exposes init/toggle APIs.
// Avoids memory leaks by tracking active voices per channel and releasing on disable.
(function(){
    const g = (window.nesSoundFont = window.nesSoundFont || {});
    let ctx; // shared AudioContext
    let outputGain; // master gain for easy mute/fade
    let initialized = false;
    let enabled = false; // runtime enable flag (SoundFontMode from .NET)
    // Disable remote sample library (reduces console noise / network failures in offline/PWA)
    let preferSampleBased = false; // set true to attempt external soundfont-player
    const REQUIRED_PROGRAMS = [80,81,32,0]; // P1,P2,TRI, fallback piano
    const LIB_URL = "https://unpkg.com/soundfont-player@0.15.2/dist/soundfont-player.js";
    let libLoadingPromise = null;
    let sampleModeReady = false;

    // Simple oscillator fallback if a real SoundFont lib not injected yet
    // (User can assign g.instrumentLoader to provide a program->play(midi,vel,dur?) interface)
    g.instrumentLoader = g.instrumentLoader || async function(program){
        // Return an object with play(note, velocity, sustainSeconds?) -> {stop()}
        return {
            play(midi, velocity, sustain){
                if(!ctx) return { stop(){}};
                const freq = 440 * Math.pow(2,(midi-69)/12);
                const osc = ctx.createOscillator();
                const gain = ctx.createGain();
                // Rough mapping: pulse1 square, pulse2 saw, triangle sine, noise -> noise node
                switch(program){
                    case 80: osc.type='square'; break; // P1
                    case 81: osc.type='sawtooth'; break; // P2
                    case 32: osc.type='triangle'; break; // TRI
                    default: osc.type='square'; break;
                }
                osc.frequency.value = freq;
                gain.gain.value = (velocity/127)*0.25; // conservative
                osc.connect(gain).connect(outputGain);
                const now = ctx.currentTime;
                osc.start(now);
                const dur = sustain || 4.0;
                osc.stop(now+dur);
                return { stop(){ try{ osc.stop(); }catch{} } };
            }
        };
    };

    const programCache = new Map();
    const gmNameMap = {
        0: 'acoustic_grand_piano',
        32: 'acoustic_bass',
        80: 'lead_1_square',
        81: 'lead_2_sawtooth'
    };

    function ensureCtx(){
        if(!ctx){ ctx = new (window.AudioContext||window.webkitAudioContext)(); }
        if(!outputGain){
            outputGain = ctx.createGain();
            // Master output gain (reduced to 50% per request to make entire soundfont quieter)
            outputGain.gain.value = 0.5;
            outputGain.connect(ctx.destination);
        }
        initialized = true;
    }

    function injectLibrary(){
        if(typeof Soundfont !== 'undefined') { sampleModeReady = true; return Promise.resolve(); }
        if(libLoadingPromise) return libLoadingPromise;
        libLoadingPromise = new Promise((resolve,reject)=>{
            const s = document.createElement('script');
            s.src = LIB_URL; s.async=true; s.onload=()=>{ sampleModeReady = typeof Soundfont !== 'undefined'; resolve(); };
            s.onerror=()=>{ console.warn('[NES] SoundFont library load failed'); reject(new Error('soundfont-player load failed')); };
            document.head.appendChild(s);
        });
        return libLoadingPromise;
    }

    async function ensureSampleMode(){
        if(!preferSampleBased) return false;
        try { await injectLibrary(); } catch { return false; }
        return sampleModeReady;
    }

    async function loadSampleInstrument(program){
        if(!sampleModeReady) return null;
        try {
            const name = gmNameMap[program] || gmNameMap[0];
            const inst = await Soundfont.instrument(ctx, name);
            return {
                play:(midi, velocity, sustain)=>{
                    const duration = sustain || 4.0;
                    const gain = velocity/127;
                    const node = inst.play(midi, ctx.currentTime, { gain, duration });
                    return { stop: ()=>{ try{ node.stop(); }catch{} } };
                }
            };
        } catch(e){ console.warn('[NES] sample instrument load fail', e); return null; }
    }
    async function getProgram(program){
        if(programCache.has(program)) return programCache.get(program);
        let inst = null;
        if(await ensureSampleMode()){
            inst = await loadSampleInstrument(program);
        }
        if(!inst){
            // fallback oscillator loader
            inst = await g.instrumentLoader(program);
        }
        programCache.set(program, inst);
        return inst;
    }

    const activeNotes = { P1:null, P2:null, TRI:null };

    g.enable = function(){
        if(enabled) return;
        ensureCtx();
        enabled = true;
        if(preferSampleBased){
            // Fire-and-forget sample mode preload
            ensureSampleMode().then(async ok=>{
                if(ok){
                    for(const p of REQUIRED_PROGRAMS){ if(!programCache.has(p)) { try { await getProgram(p);} catch{} } }
                }
            }).catch(()=>{});
        }
    };
    g.disable = function(){
        enabled = false;
        // stop any lingering active notes
        for(const k of Object.keys(activeNotes)){
            const h = activeNotes[k];
            if(h && h.stop) try{ h.stop(); }catch{}
            activeNotes[k]=null;
        }
    };

    g.handleNote = async function(channel, program, midiNote, velocity, on){
        if(!enabled) return; ensureCtx();
        if(ctx.state==='suspended'){ try{ await ctx.resume(); }catch{} }
        if(channel==='NOI'){
            // Noise one-shot: synth white noise burst
            const dur = 0.12;
            const buffer = ctx.createBuffer(1, Math.floor(ctx.sampleRate*dur), ctx.sampleRate);
            const data = buffer.getChannelData(0);
            // 25% quieter noise channel (0.35 * 0.75 = 0.2625)
            for(let i=0;i<data.length;i++){ data[i] = (Math.random()*2-1)* (velocity/127)*0.2625; }
            const src = ctx.createBufferSource(); src.buffer = buffer;
            const gNode = ctx.createGain(); gNode.gain.setValueAtTime(1, ctx.currentTime);
            gNode.gain.exponentialRampToValueAtTime(0.001, ctx.currentTime+dur);
            src.connect(gNode).connect(outputGain); src.start(); return;
        }
        const key = channel;
        // If turning off
        if(!on){
            const h = activeNotes[key];
            if(h && h.stop) { try{ h.stop(); }catch{} }
            activeNotes[key]=null; return;
        }
        // Start / retrigger
        try {
            const inst = await getProgram(program);
            // stop existing
            if(activeNotes[key] && activeNotes[key].stop) { try{ activeNotes[key].stop(); }catch{} }
            const voice = inst.play(midiNote, velocity, 4.0);
            activeNotes[key]=voice;
        } catch(e){ console.warn('play note failed', e); }
    };

    // Expose manual cleanup (called when disposing emulator or switching cores if desired)
    g.shutdown = function(){
        g.disable();
        if(ctx){ try{ ctx.close(); }catch{} }
        ctx=null; outputGain=null; initialized=false;
        programCache.clear(); sampleModeReady=false; libLoadingPromise=null;
    };

    // Public configuration APIs
    g.setPreferSampleBased = function(on){ preferSampleBased = !!on; };
    g.enableSampleMode = async function(){ preferSampleBased = true; await ensureSampleMode(); };
})();
