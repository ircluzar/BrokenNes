// Legacy shaders.js removed. This is a tiny no-op stub to avoid 404s for cached clients.
(function(){ /* no-op */ })();
// Registers shaders with nesInterop when both this script and nesInterop.js are loaded.
(function(){
  function ensure(){ return window.nesInterop && typeof window.nesInterop.registerShader === 'function'; }
  function registerAll(){
    if(!ensure()) return; // nesInterop not ready yet
    // Avoid duplicate registration
    if(window.nesInterop._shaderRegistry && Object.keys(window.nesInterop._shaderRegistry).length>0) return;

    // Minimal fallback: only register the safe pixel passthrough here.
    // The full shader set should be loaded from the /Shaders/*.frag.glsl files
    // or by the build-time shader generator. Keeping only PX avoids duplicating
    // shader sources in the static stub and prevents old cached clients from
    // shipping outdated inlined shaders.
    window.nesInterop.registerShader('PX', 'PX', `precision mediump float;varying vec2 vTex;uniform sampler2D uTex;void main(){vec2 uv=vec2(vTex.x,1.0-vTex.y);gl_FragColor=texture2D(uTex,uv);}`);
  }
  // Try now; if nesInterop not yet loaded, retry after load event
  if(!registerAll()){
    window.addEventListener('load', registerAll, { once:true });
    // Fallback retry shortly (for cases where nesInterop loads after but before load fires)
    setTimeout(registerAll, 300);
  }
})();
