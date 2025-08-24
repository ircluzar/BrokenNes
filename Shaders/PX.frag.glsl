// DisplayName: PX
// CoreName: Passthrough
// Description: Identity shader for testing and baseline comparison; flips Y to match NES texture orientation.
// Performance: 0
// Rating: 3
// Category: Utility
precision mediump float;

// PX — Passthrough (identity)
// Goal: No-op shader retaining pipeline compatibility.
// - Flips Y to match NES texture orientation
// - Returns source sample directly
// uStrength: (unused)
varying vec2 vTex;
uniform sampler2D uTex;   // Source frame
void main(){
  vec2 uv=vec2(vTex.x,1.0-vTex.y);
  gl_FragColor=texture2D(uTex,uv);
}
