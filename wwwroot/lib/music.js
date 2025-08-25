/*
  music.js — simple, shared MP3 playback with unified routing and fade support

  API (attached to window.music):
  - play(src: string, options?: { loop?: boolean, fadeInMs?: number, startAt?: number }): Promise<boolean>
  - pause(): void
  - stop(): void                                  // pause + reset position
  - fadeOut(ms?: number, thenStop?: boolean): void
  - isPlaying(): boolean
  - currentSrc(): string | ''
  - setLocalVolume(v: number): number             // 0..1 multiplier before global music bus
  - getLocalVolume(): number

  Notes:
  - Audio is routed through nesInterop’s music/master gain nodes when available
    so global volume sliders continue to work. If nesInterop isn’t present yet,
    a minimal graph is created and later bridged.
  - Fades are scheduled using WebAudio Gain nodes; we do NOT rely on <audio>.volume
    so that the global music bus continues to control absolute level.
*/
(function(){
  const ns = {};
  let ctx = null;
  let masterGain = null;     // shared master (nes)
  let musicBus = null;       // shared music bus (nes)
  let el = null;             // HTMLAudioElement
  let srcNode = null;        // MediaElementSourceNode
  let trackGain = null;      // per-track gain (for fades)
  let localVolume = 1.0;     // 0..1 multiplier before music bus
  let connectedToNes = false;
  let lastConnectWasLocal = false;

  function ensureContext(){
    try {
      // Prefer nesInterop’s context/graph when present
      if (window.nesInterop && typeof window.nesInterop.ensureAudioContext === 'function'){
        window.nesInterop.ensureAudioContext();
        ctx = window.nesAudioCtx || ctx;
        masterGain = window._nesMasterGain || masterGain;
        musicBus = window._nesMusicGain || musicBus;
        if (ctx) return ctx;
      }
    } catch {}
    if (!ctx) {
      ctx = new (window.AudioContext || window.webkitAudioContext)();
    }
    if (!masterGain){
      masterGain = ctx.createGain();
      try { masterGain.connect(ctx.destination); } catch {}
    }
    if (!musicBus){
      musicBus = ctx.createGain();
      try { musicBus.connect(masterGain); } catch {}
    }
    return ctx;
  }

  function rebuildNodesWithCtx(newCtx){
    try {
      if (!newCtx) return false;
      // Tear down existing nodes (safe disconnect only)
      try { if (srcNode) srcNode.disconnect(); } catch {}
      try { if (trackGain) trackGain.disconnect(); } catch {}
      // Switch context reference
      ctx = newCtx;
      // Recreate per-track gain and media source in the new context
      trackGain = ctx.createGain();
      try { trackGain.gain.value = localVolume; } catch {}
      try { srcNode = ctx.createMediaElementSource(el); } catch(e){
        // If this fails, keep old setup (should be rare)
        console.warn('[music] rebind: media element source failed', e);
        return false;
      }
      return true;
    } catch(e){ console.warn('[music] rebind error', e); return false; }
  }

  function bridgeToNesIfAvailable(){
    try {
      if (!(window.nesInterop && typeof window.nesInterop.ensureAudioContext === 'function')) return false;
      window.nesInterop.ensureAudioContext();
      const nesCtx = window.nesAudioCtx;
      const nesMusic = window._nesMusicGain;
      const nesMaster = window._nesMasterGain;
      if (!nesCtx || !nesMusic || !nesMaster) {
        // Don’t attempt connect on null nodes; we’ll retry later.
        return false;
      }
      // If our current context differs, rebuild nodes on nes context
      if (ctx && nesCtx && ctx !== nesCtx){
        const ok = rebuildNodesWithCtx(nesCtx);
        if (!ok) return false;
      }
      // Connect to NES buses
  try { if (srcNode) srcNode.disconnect(); } catch {}
  try { if (trackGain) trackGain.disconnect(); } catch {}
  // Ensure nodes exist before connecting
  if (!srcNode || !trackGain) return false;
  try { srcNode.connect(trackGain); } catch {}
  try { trackGain.connect(nesMusic); } catch (e){ console.warn('[music] connect to NES music bus failed', e); return false; }
      // If NES music bus is effectively mute from a prior fade, ask nesInterop to reapply saved volumes
      try {
        const gv = (nesMusic && nesMusic.gain && typeof nesMusic.gain.value === 'number') ? nesMusic.gain.value : 1;
        if (gv <= 0.0002 && window.nesInterop && typeof window.nesInterop.applySavedAudioVolumes === 'function'){
          window.nesInterop.applySavedAudioVolumes();
        }
      } catch {}
      connectedToNes = true; lastConnectWasLocal = false; return true;
    } catch(e){ console.warn('[music] bridgeToNes error', e); return false; }
  }

  function ensureElement(){
    ensureContext();
    if (!el){
      el = document.createElement('audio');
      el.preload = 'auto';
      el.crossOrigin = 'anonymous'; // harmless for same-origin
      el.style.display = 'none';
      el.setAttribute('data-music-lib', '');
      document.body.appendChild(el);
    }
    if (!trackGain){
      trackGain = ctx.createGain();
      try { trackGain.gain.value = localVolume; } catch {}
    }
    if (!srcNode){
      try {
        srcNode = ctx.createMediaElementSource(el);
      } catch(e){
        // Safari throws if we recreate after disconnect; in that case reuse previous
        if (!srcNode) throw e;
      }
    }
    // Attempt to use NES graph first; if it fails, ensure local fallback path
    let nesOk = bridgeToNesIfAvailable();
    if (!nesOk){
      // Local minimal graph (always keep a valid output)
      if (!musicBus) musicBus = ctx.createGain();
      if (!masterGain) masterGain = ctx.createGain();
      try { musicBus.disconnect(); } catch {}
      try { masterGain.disconnect(); } catch {}
      try { musicBus.connect(masterGain); } catch {}
      try { masterGain.connect(ctx.destination); } catch {}
      try { if (srcNode) srcNode.disconnect(); } catch {}
      try { if (trackGain) trackGain.disconnect(); } catch {}
      try { srcNode.connect(trackGain); trackGain.connect(musicBus); lastConnectWasLocal = true; connectedToNes = false; } catch {}
    }
    return el;
  }

  function now(){ return ctx ? ctx.currentTime : 0; }
  function clamp01(v){ return Math.max(0, Math.min(1, Number(v))); }

  ns.play = async function(src, options){
    try {
      ensureElement();
      if (ctx && ctx.state === 'suspended'){ try { await ctx.resume(); } catch {} }
      if (typeof src === 'string' && src){
        if (el.src !== src) el.src = src;
      }
      const loop = !!(options && options.loop);
      const fadeInMs = Math.max(0, Number(options && options.fadeInMs) || 0);
      const startAt = Math.max(0, Number(options && options.startAt) || 0);
      el.loop = loop;
      try { el.currentTime = startAt; } catch {}
      // Prepare fade-in on trackGain (per-track)
      const t0 = now();
      try {
        trackGain.gain.cancelScheduledValues(t0);
        if (fadeInMs > 0){
          trackGain.gain.setValueAtTime(0.0001, t0);
          trackGain.gain.linearRampToValueAtTime(clamp01(localVolume), t0 + fadeInMs/1000);
        } else {
          trackGain.gain.setValueAtTime(clamp01(localVolume), t0);
        }
      } catch {}
  // If NES became available just before play, try to bridge again (e.g., after a page route)
  if (!connectedToNes) { bridgeToNesIfAvailable(); }
  // Autoplay: start muted first (allowed by most browsers), then unmute shortly after
  try { el.muted = true; el.volume = 1.0; } catch {}
  const played = await el.play().then(() => true).catch((err) => { console.warn('[music] play() blocked or failed', err); return false; });
  // Try to unmute shortly after starting; keep fade-in on trackGain for smoothness
  setTimeout(() => { try { el.muted = false; } catch {} }, played ? 100 : 300);
      return !el.paused;
    } catch(e){
      console.warn('[music] play error', e);
      return false;
    }
  };

  ns.pause = function(){ try { if (el) el.pause(); } catch {} };

  ns.stop = function(){
    try {
      if (!el) return;
      el.pause();
      try { el.currentTime = 0; } catch {}
    } catch {}
  };

  ns.fadeOut = function(ms, thenStop){
    try {
      if (!ctx || !trackGain) return;
      const dur = Math.max(10, Number(ms)||1000) / 1000;
      const t0 = now();
      const from = trackGain.gain.value || 0;
      trackGain.gain.cancelScheduledValues(t0);
      trackGain.gain.setValueAtTime(from, t0);
      trackGain.gain.linearRampToValueAtTime(0.0001, t0 + dur);
      if (thenStop){
        setTimeout(()=>{ try { ns.stop(); } catch {} }, Math.ceil(dur*1000)+30);
      }
    } catch(e){ console.warn('[music] fadeOut error', e); }
  };

  ns.isPlaying = function(){ return !!(el && !el.paused && !el.ended); };
  ns.currentSrc = function(){ return (el && el.src) ? el.src : ''; };

  ns.setLocalVolume = function(v){
    try {
      localVolume = clamp01(v);
      if (trackGain && ctx){
        const t = now();
        trackGain.gain.cancelScheduledValues(t);
        trackGain.gain.setValueAtTime(localVolume, t);
      }
      return localVolume;
    } catch { return localVolume; }
  };
  ns.getLocalVolume = function(){ return localVolume; };

  // Expose and attempt late-bridge when nesInterop loads after us
  window.music = ns;
  // If nesInterop appears later, try to bridge automatically
  document.addEventListener('DOMContentLoaded', ()=>{ try { bridgeToNesIfAvailable(); } catch {} });
  // Also poll once after a short delay (covers late script load order)
  setTimeout(()=>{ try { bridgeToNesIfAvailable(); } catch {} }, 500);
})();
