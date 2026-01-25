// Lightweight in-browser TTS via local meSpeak.js (no browser Speech API)
// API: speak(text, { rate, pitch, volume, variant, voiceName })
// Uses the mespeak bundle you've placed under wwwroot/lib/mespeak.
(function(){
  if (window.speak) return; // idempotent

  var BASE = (typeof window.SPEAK_TTS_BASE === 'string' && window.SPEAK_TTS_BASE.trim())
    ? window.SPEAK_TTS_BASE.replace(/\/$/, '')
    : 'lib/mespeak';
  function url(p){
    p = String(p||'');
    return BASE + (p.charAt(0)==='/' ? p : ('/' + p));
  }
  // Default English voice. Options include en-us, en/en-gb, en/en-rp, etc.
  function mapVoiceId(name){
    // Map common names -> IDs matching the local voices present
    // Available: en/en-us, en/en, en/en-sc
    var key = (name||'').toLowerCase();
    if (key === 'en-us' || key === 'en_us' || key === 'us') return 'en/en-us';
    if (key === 'en-gb' || key === 'en_gb' || key === 'gb' || key === 'uk') return 'en/en';
    if (key === 'en-sc' || key === 'scotland' || key === 'sc') return 'en/en-sc';
    if (key === 'en') return 'en/en';
    return 'en/en-us';
  }
  function voiceJsonPath(voiceId){ return 'voices/' + voiceId + '.json'; }

  var ready = false;
  var loading = false;
  var pending = [];
  var chosenVoice = null; // voiceId like 'en/en-us'

  function clamp(v, lo, hi){ return Math.max(lo, Math.min(hi, v)); }

  function ensureLoaded(opts, cb){
    if (ready) { cb && cb(); return; }
    pending.push(cb);
    if (loading) return;
    loading = true;

    // 1) Load local meSpeak script
    var s = document.createElement('script');
    s.src = url('mespeak.js');
    s.async = true;
    var timeout = setTimeout(function(){ s.onerror && s.onerror(new Error('timeout')); }, 12000);
    s.onload = function(){
      clearTimeout(timeout);
      try {
        if (!window.meSpeak) { flush(new Error('meSpeak unavailable')); return; }
        // 2) Load config
        window.meSpeak.loadConfig(url('mespeak_config.json'));
        // 3) Load voice matching request or default
        var vid = mapVoiceId(opts && opts.voiceName);
        window.meSpeak.loadVoice(url(voiceJsonPath(vid)), function(vok){
          if (!vok) { flush(new Error('meSpeak voice failed')); return; }
          chosenVoice = vid;
          ready = true; flush(null);
        });
      } catch(e){ flush(e); }
    };
    s.onerror = function(){ clearTimeout(timeout); flush(new Error('Failed to load meSpeak script')); };
    document.head.appendChild(s);
  }

  function flush(err){
    var cbs = pending.slice(); pending.length = 0; loading = false;
    if (!err) ready = true;
    for (var i=0;i<cbs.length;i++){ try{ cbs[i] && cbs[i](err); }catch(e){} }
  }

  function mapToMultipartOpts(text, optsOrSpeed){
    var opts = (typeof optsOrSpeed === 'number') ? { speed: optsOrSpeed } : (optsOrSpeed || {});
    var clamp = function(v, lo, hi){ return Math.max(lo, Math.min(hi, v)); };
    // Prefer explicit speed like speakcode.js; else map rate -> speed (baseline 125)
    var speed = (opts.speed != null) ? +opts.speed
               : (opts.rate != null) ? clamp(Math.round(125 * +opts.rate), 80, 450)
               : 125;
    // speakcode uses pitch 1 neutral
    var pitch = (opts.pitch == null) ? 1 : +opts.pitch;
  // speakcode amplitude: gameValues["VoiceVolume"] * 0.45 (optionally scaled by provided volume)
  // In this app, gameValues may be undefined or normalized (0..1). Default to a loud-but-safe base.
  var gv = 200;
  try { if (window.gameValues && typeof window.gameValues["VoiceVolume"] === 'number') gv = window.gameValues["VoiceVolume"]; } catch {}
  // If it looks normalized (<=2), scale to mespeak's 0..200 domain
  if (gv <= 2) gv = gv * 200;
  var vol = (opts.volume == null) ? 1 : +opts.volume;
  var amplitude = Math.max(0, Math.min(200, gv * vol * 0.45));
    var variant = opts.variant || 'croak';
    var voiceId = mapVoiceId(opts.voiceName);
    return [{
      text: String(text||''),
      voice: voiceId,
      variant: variant,
      amplitude: amplitude,
      pitch: pitch,
      speed: speed
    }];
  }

  function speakNow(text, opts){
    try{
      if (!window.meSpeak || !ready) return;
      // Voice switch on demand
      var vid = mapVoiceId(opts && opts.voiceName);
      if (vid && vid !== chosenVoice){
        window.meSpeak.loadVoice(url(voiceJsonPath(vid)), function(vok){
          if (vok) { chosenVoice = vid; try { playViaWebAudio(String(text||''), opts); } catch(e){} }
        });
        return;
      }
      playViaWebAudio(String(text||''), opts);
    }catch(e){ console.error('meSpeak speak failed', e); }
  }

  // --- WebAudio routing ---
  var ttsCtx = null; var ttsGain = null;
  function ensureTtsAudio(){
    try {
      if (window.nesInterop && typeof window.nesInterop.ensureAudioContext === 'function'){
        window.nesInterop.ensureAudioContext();
      }
    } catch {}
    if (!ttsCtx) ttsCtx = window.nesAudioCtx || new (window.AudioContext || window.webkitAudioContext)();
    if (!ttsGain && ttsCtx){
      try { ttsGain = ttsCtx.createGain(); ttsGain.gain.value = 1; } catch {}
      try {
        if (window._nesMusicGain) { ttsGain.connect(window._nesMusicGain); }
        else { ttsGain.connect(ttsCtx.destination); }
      } catch {}
    }
    return ttsCtx;
  }

  function decodeDataUrlToArrayBuffer(dataUrl){
    try {
      var idx = dataUrl.indexOf(',');
      var b64 = dataUrl.slice(idx+1);
      var bin = atob(b64);
      var len = bin.length; var buf = new ArrayBuffer(len); var view = new Uint8Array(buf);
      for (var i=0;i<len;i++) view[i] = bin.charCodeAt(i);
      return buf;
    } catch { return null; }
  }

  function playArrayBuffer(buf, onDone){
    var ctx = ensureTtsAudio(); if (!ctx) return;
    if (ctx.state === 'suspended') { try { ctx.resume(); } catch {} }
    try {
      var decode = (ctx.decodeAudioData.length === 1)
        ? new Promise(function(res, rej){ try { res(ctx.decodeAudioData(buf)); } catch(e){ rej(e); } })
        : new Promise(function(res, rej){ ctx.decodeAudioData(buf, res, rej); });
      decode.then(function(audioBuffer){
        var src = ctx.createBufferSource(); src.buffer = audioBuffer;
        try { src.connect(ttsGain || ctx.destination); } catch {}
        src.onended = function(){ try { if (onDone) onDone(); } catch {} };
        try { src.start(0); } catch {}
      }).catch(function(e){ try { if (window.DEBUG_TTS) console.warn('[tts] decode failed', e); } catch {} });
    } catch(e){ try { if (window.DEBUG_TTS) console.warn('[tts] playArrayBuffer error', e); } catch {} }
  }

  function playViaWebAudio(text, opts){
    try {
      // Build meSpeak options for raw output
      var parts = mapToMultipartOpts(text, opts);
      var p = parts[0] || {};
      var o = {
        voice: p.voice,
        variant: p.variant,
        amplitude: p.amplitude,
        pitch: p.pitch,
        speed: p.speed,
        rawdata: 'arraybuffer'
      };
      var wav = window.meSpeak.speak(text, o);
      if (wav && (wav instanceof ArrayBuffer)) { playArrayBuffer(wav); return; }
      // Some builds return data-url when rawdata unsupported
      if (typeof wav === 'string' && wav.indexOf('data:audio') === 0){
        var buf = decodeDataUrlToArrayBuffer(wav); if (buf) { playArrayBuffer(buf); return; }
      }
      // Fallback: let mespeak manage playback
      try { window.meSpeak.speakMultipart(parts); } catch {}
    } catch(e){ try { window.meSpeak.speakMultipart(mapToMultipartOpts(text, opts)); } catch {} }
  }

  function speak(text, opts){
    // Queue until synth is ready
    ensureLoaded(opts, function(err){
      if (err) { console.warn('TTS fallback disabled:', err && err.message); return; }
  try { if (window.DEBUG_TTS) console.info('[tts] speak', { text: String(text||'').slice(0,64), opts: opts }); } catch {}
  speakNow(text, opts);
    });
  }

  window.speak = speak;
  window.speakPreload = function(opts){ ensureLoaded(opts, function(){}); };
  // Provide the same helpers as in speakcode.js
  window.speakit = function(text){ speak(text, { speed: 125 }); };
  window.speakitspeed = function(text, speed){ speak(text, Number(speed)||125); };
})();
