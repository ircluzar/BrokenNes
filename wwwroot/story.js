// Story mode script with simple Do/Wait scheduler and TTS init
(function(){
  try {
    // Ensure speak.js is initialized immediately
    function loadSpeak(cb){
      try{
        if (window.speak) { cb && cb(); return; }
        var s = document.createElement('script');
        s.src = '/speak.js';
        s.async = true;
        s.onload = function(){ try{ cb && cb(); }catch(e){} };
        s.onerror = function(){ try{ cb && cb(); }catch(e){} };
        document.head.appendChild(s);
      }catch(e){ cb && cb(); }
    }

    // Tiny chainable scheduler: Do(fn), Wait(seconds), Start(), Reset()
    var queue = []; var idx = 0; var started = false;
    function Do(fn){ queue.push({ t:'do', fn: fn }); return api; }
    function Wait(seconds){ var ms = Math.max(0, (seconds||0)*1000); queue.push({ t:'wait', ms: ms }); return api; }
    function _run(){
      if (idx >= queue.length) return;
      var it = queue[idx++];
      if (it.t === 'do') { try { it.fn && it.fn(); } catch(e){} _run(); }
      else { setTimeout(_run, it.ms); }
    }
    function Start(){ if (started) return; started = true; _run(); }
    function Reset(){ idx = 0; started = false; }
    var api = { Do: Do, Wait: Wait, Start: Start, Reset: Reset };
    window.StorySchedule = api; // expose for story scripting
    window.Do = Do; window.Wait = Wait;

    // Kick things off once speak is ready
    loadSpeak(function(){
      try {
  // Start background story music immediately (loop w/ gentle fade-in)
  try { if (window.music && typeof window.music.play === 'function') { window.music.play('music/Story.mp3', { loop: true, fadeInMs: 800 }); } } catch(e){}

        if (queue.length === 0) {
          // Default story: first line, then continue with three more lines spaced by 4s
          Wait(3).Do(function(){
            try { window.speak && window.speak('All that little Timmy wanted was a functional video game console.', { rate: 0.69, pitch: 0.69 }); } catch(e){}
          })
          .Wait(7).Do(function(){
            try { window.speak && window.speak('But every cartridge he tried sputtered and blinked.', { rate: 0.69, pitch: 0.69 }); } catch(e){}
          })
          .Wait(7).Do(function(){
            try { window.speak && window.speak('He took a deep breath, flipped the switch once more.', { rate: 0.69, pitch: 0.69 }); } catch(e){}
          })
          .Wait(7).Do(function(){
            try { window.speak && window.speak('Somewhere between the static and the scanlines.', { rate: 0.69, pitch: 0.69 }); } catch(e){}
          });
        }
        Start();
      } catch(e) { try { Start(); } catch(_){} }
    });
  } catch (e) {
    console.error('story.js failed', e);
  }
})();
