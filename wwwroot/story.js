// Story mode script with simple Do/Wait scheduler and TTS init
(function(){
  try {
    // Ensure speak.js is initialized immediately
    function loadSpeak(cb){
      try{
        if (window.speak) { cb && cb(); return; }
  var s = document.createElement('script');
  // Use relative path so it respects <base href>
  s.src = 'speak.js';
        s.async = true;
        s.onload = function(){ try{ cb && cb(); }catch(e){} };
        s.onerror = function(){ try{ cb && cb(); }catch(e){} };
        document.head.appendChild(s);
      }catch(e){ cb && cb(); }
    }

    // Tiny chainable scheduler: supports sync and async Do steps
    // API: Do(fn|asyncFn), Wait(seconds), Start(), Reset()
    var queue = []; var idx = 0; var started = false;
    function Do(fn){ queue.push({ t:'do', fn: fn }); return api; }
    function Wait(seconds){ var ms = Math.max(0, (seconds||0)*1000); queue.push({ t:'wait', ms: ms }); return api; }
    function _run(){
      if (idx >= queue.length) return;
      var it = queue[idx++];
      if (it.t === 'do') {
        try {
          var res = it.fn && it.fn();
          if (res && typeof res.then === 'function') { res.then(function(){ _run(); }).catch(function(){ _run(); }); }
          else { _run(); }
        } catch(_) { _run(); }
      } else {
        setTimeout(_run, it.ms);
      }
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

        // Preload local meSpeak (avoids first-line delay)
        try { if (window.speakPreload) window.speakPreload({ voiceName: 'en-us' }); } catch {}

        // Ensure a subtitles box exists under the emulator viewport
        function ensureSubtitlesHost(){
          try{
            var host = document.getElementById('storySubtitles');
            if (host) return host;
            var aspect = document.querySelector('.screen-shell .screen-aspect');
            var shell = aspect ? aspect.parentElement : null;
            host = document.createElement('div');
            host.id = 'storySubtitles';
            host.setAttribute('role','log');
            host.setAttribute('aria-live','polite');
            // Basic styling: centered text on a translucent band below the canvas
            host.style.marginTop = '6px';
            host.style.padding = '6px 10px';
            host.style.background = 'rgba(0,0,0,0.55)';
            host.style.color = '#fff';
            host.style.textAlign = 'center';
            host.style.fontSize = '14px';
            host.style.lineHeight = '1.35';
            host.style.borderRadius = '6px';
            host.style.textShadow = '0 1px 2px rgba(0,0,0,.8)';
            host.style.minHeight = '20px';
            try{
              if (shell && aspect && shell.insertBefore) {
                shell.insertBefore(host, aspect.nextSibling);
              } else if (shell && shell.appendChild) {
                shell.appendChild(host);
              } else {
                // Fallback: append to body bottom
                document.body.appendChild(host);
              }
            }catch(e){ try{ document.body.appendChild(host); }catch(_){} }
            return host;
          }catch(_){ return null; }
        }
        function setSubtitle(text){
          try{
            var host = ensureSubtitlesHost();
            if (!host) return;
            host.textContent = text || '';
          }catch(_){}
        }

        // Helper to call C# [JSInvokable] to load a built-in ROM without registration
        window.loadNarrationRom = async function(name){
          try {
            // Try to reuse the main DotNet ref if nesInterop exposed it
            var ref = (window.nesInterop && window.nesInterop._mainRef) ? window.nesInterop._mainRef : null;
            if (ref && typeof ref.invokeMethodAsync === 'function') {
              var ok = await ref.invokeMethodAsync('JsLoadBuiltInRom', name);
              return !!ok;
            }
            // Fallback: static invoke on the assembly (requires export); if not available, no-op
            if (window.DotNet && typeof window.DotNet.invokeMethodAsync === 'function') {
              try { var ok2 = await window.DotNet.invokeMethodAsync('BrokenNes', 'JsLoadBuiltInRom', name); return !!ok2; } catch(e) {}
            }
          } catch(e) { /* swallow */ }
          return false;
        };

        if (queue.length === 0) {
          // Default story; load pageX.nes before each narration line
          function narrate(t){
            try {
              setSubtitle(t);
              if (window.speakit) window.speakit(t);
              else if (window.speak) window.speak(t, { speed: 125, variant: 'croak', voiceName: 'en-us' });
            } catch(e){}
          }
          Do(function(){ setSubtitle(' '); })
          .Wait(2)
          .Do(async function(){ await window.loadNarrationRom('page1.nes'); })
          .Do(function(){ narrate('All that little Timmy wanted was a functional video game console.'); })
          .Wait(6)
          .Do(async function(){ await window.loadNarrationRom('page2.nes'); })
          .Do(function(){ narrate('But his mom would keep buying him janky clones instead.'); })
          .Wait(6)
          .Do(async function(){ await window.loadNarrationRom('page3.nes'); })
          .Do(function(){ narrate('So little Timmy broke them all into parts.'); })
          .Wait(6)
          .Do(async function(){ await window.loadNarrationRom('page4.nes'); })
          .Do(function(){ narrate('And now, he is ready to build his ultimate console.'); })
          .Wait(5)
          .Do(async function(){ await window.loadNarrationRom('page5.nes'); })
          .Wait(7)
          .Do(function(){
            try{
              // Quick fade to black then navigate to Continue page
              var ov = document.getElementById('storyFadeOverlay');
              if(!ov){
                ov = document.createElement('div');
                ov.id = 'storyFadeOverlay';
                ov.style.position='fixed'; ov.style.inset='0'; ov.style.background='#000'; ov.style.opacity='0';
                ov.style.transition='opacity .6s ease'; ov.style.pointerEvents='none'; ov.style.zIndex='99999';
                document.body.appendChild(ov);
              }
              // Clear subtitle on exit
              try { setSubtitle(''); } catch(_){ }
              requestAnimationFrame(function(){ ov.style.opacity='1'; });
              setTimeout(function(){ try { window.location.href = 'continue'; } catch { window.location.assign('continue'); } }, 650);
            }catch(_){ window.location.href = 'continue'; }
          });
        }
        Start();
      } catch(e) { try { Start(); } catch(_){} }
    });
  } catch (e) {
    console.error('story.js failed', e);
  }
})();
