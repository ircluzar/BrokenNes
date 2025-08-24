// Simple TTS helper using the Web Speech API
// Usage: speak(text, {lang, rate, pitch, volume, voiceName})
(function(){
  if (window.speak) return; // idempotent
  function getVoiceByName(name){
    var list = window.speechSynthesis ? window.speechSynthesis.getVoices() : [];
    if (!name || !list || !list.length) return null;
    for (var i=0;i<list.length;i++){ if (list[i].name === name) return list[i]; }
    return null;
  }
  function speak(text, opts){
    try{
      if (!('speechSynthesis' in window)) { console.warn('speechSynthesis not supported'); return; }
      var u = new SpeechSynthesisUtterance(text || '');
      opts = opts || {};
      if (opts.lang) u.lang = opts.lang;
      if (opts.rate != null) u.rate = opts.rate;
      if (opts.pitch != null) u.pitch = opts.pitch;
      if (opts.volume != null) u.volume = opts.volume;
      if (opts.voiceName) {
        var v = getVoiceByName(opts.voiceName);
        if (v) u.voice = v; else console.warn('Voice not found:', opts.voiceName);
      }
      // Voices may not be loaded immediately; try again onvoiceschanged once.
      if ((!u.voice && opts.voiceName) || (window.speechSynthesis && window.speechSynthesis.getVoices().length===0)){
        var once = function(){ try{ window.speechSynthesis.onvoiceschanged = null; }catch(e){} speak(text, opts); };
        try { window.speechSynthesis.onvoiceschanged = once; } catch(e) { }
      }
      window.speechSynthesis.cancel(); // stop anything pending
      window.speechSynthesis.speak(u);
    }catch(e){ console.error('speak failed', e); }
  }
  window.speak = speak;
})();
