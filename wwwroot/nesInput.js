// nesInput.js - helpers for input settings UI and controller discovery
window.nesInput = (function(){
  const mod = {};
  mod.getGamepads = function(){
    try{
      const list = navigator.getGamepads ? Array.from(navigator.getGamepads()) : [];
      return (list||[]).filter(Boolean).map((g,i)=>({
        index: g.index ?? i,
        id: String(g.id||'Gamepad '+(g.index??i)),
        connected: !!g.connected,
        buttons: (g.buttons||[]).length|0,
        axes: (g.axes||[]).length|0
      }));
    }catch(e){ return []; }
  }

  // Listen once to connection changes to encourage browsers to populate
  if(typeof window !== 'undefined'){
    const poke = ()=>{};
    window.addEventListener('gamepadconnected', poke);
    window.addEventListener('gamepaddisconnected', poke);
  }

  // simple key capture helper for UI: returns next key code pressed
  mod.captureNextKey = function(){
    return new Promise(resolve=>{
      const handler = (e)=>{
        e.preventDefault();
        window.removeEventListener('keydown', handler, true);
        resolve(e.code||e.key||'');
      };
      window.addEventListener('keydown', handler, true);
    });
  };

  return mod;
})();
